using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dhole.Agent.Api.Extensions;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.Api.Endpoints;

/// <summary>
/// Short-lived, authenticated bridge to the existing loopback-only standby
/// noVNC viewer. Tickets are HttpOnly cookies, never URLs or localStorage.
/// Does not expose CDP, Docker, profiles, credentials or queue Workers.
/// </summary>
internal static class StandbyViewerGateway
{
    private sealed record Grant(string Generation, Guid Actor, DateTimeOffset Expires);
    private static readonly ConcurrentDictionary<string, Grant> Grants = new();
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(10);
    private const string Root = "/api/agents/runtime-generations/authentication/";

    private static bool ValidGeneration(string name) =>
        Regex.IsMatch(name, @"^prod(?:[2-9]|[1-9][0-9]{1,2})$") &&
        int.TryParse(name.AsSpan(4), out var n) && n <= 200;

    private static string Prefix(string generation) => Root + generation + "/viewer";
    private static string CookieName(string generation) => "dhole_standby_viewer_" + generation;
    private static string Key(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // The API can route a request to the browser-only container on the private
    // Docker network. Chromium CDP (9222) remains unreachable from here.
    private static string InternalViewer(string generation) =>
        "http://dhole-standby-auth-" + generation + ":6080/";

    public static async Task<IResult> IssueAsync(
        string generation, HttpContext context, ServiceDbContext db,
        CancellationToken cancellationToken)
    {
        var actor = context.GetCurrentUserId();
        if (actor is null || actor == Guid.Empty) return Results.Unauthorized();
        if (!ValidGeneration(generation)) return Results.BadRequest(new { code = "invalid_generation" });
        if (!await AuthorizedAsync(db, generation, actor.Value, cancellationToken))
            return Results.Conflict(new { code = "standby_authentication_not_ready" });

        // Never issue a ticket for an absent browser.
        try
        {
            using var response = await Http.GetAsync(InternalViewer(generation) + "vnc.html",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Results.Conflict(new { code = "standby_viewer_unavailable" });
        }
        catch (HttpRequestException)
        {
            return Results.Conflict(new { code = "standby_viewer_unavailable" });
        }
        catch (TaskCanceledException)
        {
            return Results.Conflict(new { code = "standby_viewer_unavailable" });
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var until = DateTimeOffset.UtcNow.Add(Life);
        Grants[Key(token)] = new Grant(generation, actor.Value, until);
        foreach (var entry in Grants)
            if (entry.Value.Expires <= DateTimeOffset.UtcNow)
                Grants.TryRemove(entry.Key, out _);

        context.Response.Cookies.Append(CookieName(generation), token, new CookieOptions
        {
            HttpOnly = true,
            Secure = !context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(),
            SameSite = context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()
                ? SameSiteMode.Lax : SameSiteMode.None,
            Path = Prefix(generation),
            MaxAge = Life,
            IsEssential = true
        });
        context.Response.Headers.CacheControl = "no-store";
        var path = Prefix(generation);
        return Results.Ok(new
        {
            url = path + "/vnc.html?autoconnect=true&resize=scale&path=" +
                Uri.EscapeDataString(path.TrimStart('/') + "/websockify"),
            expiresAtUtc = until
        });
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        // Browser WebSocket and iframe requests cannot attach JWT headers.
        // These endpoints authenticate an independently issued short-lived
        // cookie, granted only by the bearer-authenticated, scoped API.
        app.MapGet(Root + "{generation}/viewer/{**asset}",
            async (HttpContext context, ServiceDbContext db,
                   string generation, string? asset, CancellationToken ct) =>
                await ProxyAsync(context, db, generation, asset ?? "vnc.html", ct))
           .AllowAnonymous();
    }

    private static bool ValidAsset(string asset) =>
        asset.Length is >= 1 and <= 240 &&
        !asset.StartsWith('/') && !asset.Contains("..", StringComparison.Ordinal) &&
        !asset.Contains("//", StringComparison.Ordinal) &&
        Regex.IsMatch(asset, @"^[A-Za-z0-9_./-]+$") &&
        (asset == "vnc.html" || asset == "favicon.ico" ||
         asset.StartsWith("app/", StringComparison.Ordinal) ||
         asset.StartsWith("core/", StringComparison.Ordinal) ||
         asset.StartsWith("vendor/", StringComparison.Ordinal) ||
         asset.StartsWith("images/", StringComparison.Ordinal) ||
         asset == "websockify");

    private static async Task ProxyAsync(
        HttpContext context, ServiceDbContext db, string generation,
        string asset, CancellationToken cancellationToken)
    {
        if (!ValidGeneration(generation) || !ValidAsset(asset) ||
            !TryGrant(context, generation, out var grant) ||
            !await AuthorizedAsync(db, generation, grant.Actor, cancellationToken))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.Remove("X-Frame-Options");
        context.Response.Headers["Content-Security-Policy"] =
            "frame-ancestors 'self' https://dhole.customcodecr.com " +
            "https://sistema.logisticacastrofallas.com http://localhost:5173";

        if (asset == "websockify")
        {
            if (!context.WebSockets.IsWebSocketRequest || !AllowedOrigin(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            using var upstream = new ClientWebSocket();
            try
            {
                await upstream.ConnectAsync(new Uri(InternalViewer(generation)
                    .Replace("http://", "ws://") + "websockify"), cancellationToken);
                using var client = await context.WebSockets.AcceptWebSocketAsync();
                using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var one = RelayAsync(client, upstream, relayCancellation.Token);
                var two = RelayAsync(upstream, client, relayCancellation.Token);
                await Task.WhenAny(one, two);
                relayCancellation.Cancel();
                try { await Task.WhenAll(one, two); }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            }
            catch (WebSocketException)
            {
                if (!context.Response.HasStarted)
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
            return;
        }

        using var response = await Http.GetAsync(
            InternalViewer(generation) + asset,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString()
            ?? "application/octet-stream";
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }

    private static async Task RelayAsync(
        WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[32_768];
        while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
        {
            var received = await source.ReceiveAsync(
                new ArraySegment<byte>(buffer), cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                return;
            await destination.SendAsync(
                new ArraySegment<byte>(buffer, 0, received.Count),
                received.MessageType, received.EndOfMessage, cancellationToken);
        }
    }

    private static bool AllowedOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;
        return (uri.Scheme == "https" && (uri.Host == "dhole.customcodecr.com" ||
            uri.Host == "sistema.logisticacastrofallas.com")) ||
            (uri.Scheme == "http" && (uri.Host == "localhost" ||
            uri.Host == "127.0.0.1") && uri.Port == 5173) ||
            // The API is normally behind TLS termination. Its internal
            // Request.Scheme can be http while the browser Origin is https.
            (uri.Scheme == "https" &&
            uri.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGrant(HttpContext context, string generation, out Grant grant)
    {
        grant = null!;
        if (!context.Request.Cookies.TryGetValue(CookieName(generation), out var token) ||
            token.Length != 64 || !Regex.IsMatch(token, @"^[A-F0-9]{64}$") ||
            !Grants.TryGetValue(Key(token), out var existing) ||
            existing.Generation != generation || existing.Expires <= DateTimeOffset.UtcNow)
            return false;
        grant = existing;
        return true;
    }

    private static async Task<bool> AuthorizedAsync(
        ServiceDbContext db, string generation, Guid actor, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM agent.runtime_generation_auth_sessions s
                    WHERE s.generation=@generation AND s.actor_id=@actor
                      AND s.status='Ready'
                      AND s.requested_at_utc > now() - interval '90 minutes'
                      AND NOT EXISTS (
                          SELECT 1 FROM agent.runtime_generation_state r
                          WHERE r.singleton=true AND r.active_generation=s.generation
                      )
                );
                """;
            var generationParam = command.CreateParameter();
            generationParam.ParameterName = "generation";
            generationParam.Value = generation;
            command.Parameters.Add(generationParam);
            var actorParam = command.CreateParameter();
            actorParam.ParameterName = "actor";
            actorParam.Value = actor;
            command.Parameters.Add(actorParam);
            var result = (await command.ExecuteScalarAsync(ct))?.ToString();
            return result == "True" || result == "true" || result == "t";
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
