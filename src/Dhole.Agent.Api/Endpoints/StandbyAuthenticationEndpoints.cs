using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CustomCodeFramework.Api.Responses;
using Dhole.Agent.Api.Authorization;
using Dhole.Agent.Api.Extensions;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.Api.Endpoints;

/// <summary>
/// Pre-authorize a dormant generation without transferring queue ownership.
/// GitHub performs Docker operations using the self-hosted trusted runner.
/// </summary>
internal static class StandbyAuthenticationEndpoints
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS agent.runtime_generation_auth_sessions (
            generation text PRIMARY KEY,
            credential_id uuid NOT NULL,
            actor_id uuid NOT NULL,
            status text NOT NULL CHECK (status IN
                ('Requested','Preparing','Ready','Finishing','Verified','Expired','Failed')),
            requested_at_utc timestamptz NOT NULL DEFAULT now(),
            updated_at_utc timestamptz NOT NULL DEFAULT now(),
            diagnostic_code text NULL
        );
        ALTER TABLE agent.runtime_generation_auth_sessions
          ADD COLUMN IF NOT EXISTS account_name text,
          ADD COLUMN IF NOT EXISTS account_username text,
          ADD COLUMN IF NOT EXISTS account_customer_id text,
          ADD COLUMN IF NOT EXISTS account_verified_at_utc timestamptz;
        CREATE UNIQUE INDEX IF NOT EXISTS ix_generation_single_pre_auth
          ON agent.runtime_generation_auth_sessions((true))
          WHERE status IN ('Requested','Preparing','Ready','Finishing');
        """;

    internal sealed record PrepareStandbyAuthentication(Guid CredentialId);
    internal sealed record CompleteStandbyAuthentication(string Confirmation);
    private const string Confirm = "I_VERIFIED_AUTHORIZED_MAERSK_ACCESS_IN_THIS_STANDBY";

    public static void MapStandbyAuthenticationEndpoints(this RouteGroupBuilder root)
    {
        var group = root.MapGroup("/runtime-generations/authentication");
        group.MapGet("/", async (ServiceDbContext db, CancellationToken ct) =>
        {
            await ExpireAsync(db, ct);
            var rows = await ReadRowsAsync(db, """
                SELECT generation, status, diagnostic_code,
                       requested_at_utc::text, updated_at_utc::text,
                       account_name, account_username, account_customer_id,
                       account_verified_at_utc::text
                FROM agent.runtime_generation_auth_sessions
                ORDER BY requested_at_utc DESC LIMIT 50
                """, ct);
            return Results.Ok(ApiResponse<object>.Ok(rows.Select(x => new
            {
                generation = x[0], status = x[1], diagnosticCode = x[2],
                requestedAtUtc = x[3], updatedAtUtc = x[4],
                accountName = x[5], accountUsername = x[6],
                accountCustomerId = x[7], accountVerifiedAtUtc = x[8]
            }).ToArray()));
        }).RequireScope(AgentScopeNames.BrowserProfilesView);

        group.MapGet("/credentials", async (ServiceDbContext db, CancellationToken ct) =>
        {
            var credentials = await db.AgentCredentials.AsNoTracking()
                .Join(db.AgentProviders.AsNoTracking(), c => c.ProviderId, p => p.Id,
                    (c, p) => new { Credential = c, Provider = p })
                .Where(x => x.Credential.IsActive && !x.Credential.IsDeleted &&
                    x.Provider.IsActive && !x.Provider.IsDeleted && x.Provider.Code == "MAERSK")
                .Select(x => new { id = x.Credential.Id, name = x.Credential.Name })
                .ToListAsync(ct);
            return Results.Ok(ApiResponse<object>.Ok(credentials));
        }).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);

        group.MapPost("/{generation}/prepare", async (
            string generation, PrepareStandbyAuthentication request,
            HttpContext http, ServiceDbContext db, IConfiguration configuration, CancellationToken ct) =>
        {
            if (http.GetCurrentUserId() is not { } actor || actor == Guid.Empty)
                return Results.Unauthorized();
            if (!ValidTarget(generation) || request.CredentialId == Guid.Empty)
                return Results.BadRequest(new { code = "invalid_standby_authentication" });
            var token = configuration["GenerationOrchestrator:GitHubToken"];
            if (!configuration.GetValue<bool>("GenerationOrchestrator:Enabled")
                || string.IsNullOrWhiteSpace(token))
                return Results.Conflict(new { code = "github_dispatcher_not_configured" });
            await ExpireAsync(db, ct);
            var eligible = await ScalarAsync(db, """
                SELECT EXISTS (
                    SELECT 1 FROM agent.runtime_generation_provisions p
                    JOIN agent.runtime_generations g ON g.name=p.generation
                    WHERE p.generation=@generation AND p.status='Provisioned'
                )::text
                """, ct, ("generation", generation));
            var current = await ScalarAsync(db,
                "SELECT active_generation FROM agent.runtime_generation_state WHERE singleton=true", ct);
            if (eligible != "true" || current == generation)
                return Results.Conflict(new { code = "standby_not_provisioned_or_already_active" });

            var credential = await db.AgentCredentials.AsNoTracking()
                .Join(db.AgentProviders.AsNoTracking(), c => c.ProviderId, p => p.Id,
                    (c, p) => new { Credential = c, Provider = p })
                .AnyAsync(x => x.Credential.Id == request.CredentialId
                    && x.Credential.IsActive && !x.Credential.IsDeleted
                    && x.Provider.Code == "MAERSK" && x.Provider.IsActive && !x.Provider.IsDeleted, ct);
            if (!credential)
                return Results.Conflict(new { code = "standby_maersk_credential_not_authorized" });
            try
            {
                var changed = await ScalarAsync(db, """
                    INSERT INTO agent.runtime_generation_auth_sessions
                        (generation,credential_id,actor_id,status)
                    VALUES (@generation,@credential,@actor,'Requested')
                    ON CONFLICT(generation) DO UPDATE SET
                        credential_id=EXCLUDED.credential_id,
                        actor_id=EXCLUDED.actor_id,
                        status='Requested',
                        requested_at_utc=now(),updated_at_utc=now(),
                        diagnostic_code=NULL,
                        account_name=NULL,account_username=NULL,
                        account_customer_id=NULL,account_verified_at_utc=NULL
                    WHERE runtime_generation_auth_sessions.status NOT IN
                        ('Requested','Preparing','Ready','Finishing')
                    RETURNING generation
                    """, ct, ("generation", generation),
                    ("credential", request.CredentialId), ("actor", actor));
                if (changed is null)
                    return Results.Conflict(new { code = "authentication_preparation_already_active" });
            }
            catch (DbException)
            {
                return Results.Conflict(new { code = "other_standby_authentication_in_progress" });
            }
            if (!await DispatchAsync(token, "prepare", generation, request.CredentialId, "PREPARE_STANDBY_AUTHENTICATION", ct))
            {
                await UpdateStatusAsync(db, generation, "Requested", "Failed", "github_dispatch_failed", ct);
                return Results.Conflict(new { code = "standby_authentication_dispatch_failed" });
            }
            return Results.Accepted("/api/agents/runtime-generations/authentication/",
                new { generation, status = "Requested", activeGenerationUnchanged = true });
        }).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);

        // Actual provider proof is produced by a read-only CDP probe inside
        // this generation's temporary browser (not by the active prod1 Worker).
        group.MapPost("/{generation}/inspect", async (
            string generation, HttpContext http, ServiceDbContext db,
            IConfiguration configuration, CancellationToken ct) =>
        {
            if (http.GetCurrentUserId() is not { } actor || actor == Guid.Empty)
                return Results.Unauthorized();
            if (!ValidTarget(generation))
                return Results.BadRequest(new { code = "invalid_generation" });
            var token = configuration["GenerationOrchestrator:GitHubToken"];
            if (!configuration.GetValue<bool>("GenerationOrchestrator:Enabled") ||
                string.IsNullOrWhiteSpace(token))
                return Results.Conflict(new { code = "github_dispatcher_not_configured" });
            await ExpireAsync(db, ct);
            var credential = await ScalarAsync(db, """
                SELECT credential_id::text
                FROM agent.runtime_generation_auth_sessions
                WHERE generation=@generation AND actor_id=@actor AND status='Ready'
                  AND requested_at_utc>now()-interval '30 minutes'
                """, ct, ("generation", generation), ("actor", actor));
            if (credential is null || !Guid.TryParse(credential, out var id))
                return Results.Conflict(new { code = "standby_authentication_not_ready" });
            if (!await DispatchAsync(token, "inspect", generation, id,
                "INSPECT_STANDBY_ACCOUNT", ct))
                return Results.Conflict(new { code = "standby_identity_dispatch_failed" });
            return Results.Accepted("/api/agents/runtime-generations/authentication/",
                new { generation, operation = "inspect", activeGenerationUnchanged = true });
        }).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);

        group.MapPost("/{generation}/restart", async (
            string generation, HttpContext http, ServiceDbContext db,
            IConfiguration configuration, CancellationToken ct) =>
        {
            if (http.GetCurrentUserId() is not { } actor || actor == Guid.Empty)
                return Results.Unauthorized();
            if (!ValidTarget(generation))
                return Results.BadRequest(new { code = "invalid_generation" });
            var token = configuration["GenerationOrchestrator:GitHubToken"];
            if (!configuration.GetValue<bool>("GenerationOrchestrator:Enabled") ||
                string.IsNullOrWhiteSpace(token))
                return Results.Conflict(new { code = "github_dispatcher_not_configured" });
            await ExpireAsync(db, ct);
            var credential = await ScalarAsync(db, """
                UPDATE agent.runtime_generation_auth_sessions
                SET status='Preparing', updated_at_utc=now(),
                    account_name=NULL, account_username=NULL,
                    account_customer_id=NULL,account_verified_at_utc=NULL
                WHERE generation=@generation AND actor_id=@actor AND status='Ready'
                  AND requested_at_utc>now()-interval '30 minutes'
                RETURNING credential_id::text
                """, ct, ("generation", generation), ("actor", actor));
            if (credential is null || !Guid.TryParse(credential, out var id))
                return Results.Conflict(new { code = "standby_authentication_not_ready" });
            if (!await DispatchAsync(token, "restart", generation, id,
                "RESTART_STANDBY_ACCOUNT_VIEWER", ct))
            {
                await UpdateStatusAsync(db, generation, "Preparing", "Ready",
                    "github_dispatch_failed", ct);
                return Results.Conflict(new { code = "standby_restart_dispatch_failed" });
            }
            return Results.Accepted("/api/agents/runtime-generations/authentication/",
                new { generation, operation = "restart", activeGenerationUnchanged = true });
        }).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);

        group.MapPost("/{generation}/complete", async (
            string generation, CompleteStandbyAuthentication request,
            HttpContext http, ServiceDbContext db, IConfiguration configuration, CancellationToken ct) =>
        {
            if (http.GetCurrentUserId() is not { } actor || actor == Guid.Empty)
                return Results.Unauthorized();
            if (!ValidTarget(generation) || request.Confirmation != Confirm)
                return Results.BadRequest(new { code = "independent_operator_confirmation_required" });
            var token = configuration["GenerationOrchestrator:GitHubToken"];
            if (!configuration.GetValue<bool>("GenerationOrchestrator:Enabled")
                || string.IsNullOrWhiteSpace(token))
                return Results.Conflict(new { code = "github_dispatcher_not_configured" });
            await ExpireAsync(db, ct);
            // A readiness attestation is forbidden unless the operator explicitly
            // confirms legitimate access in the STILL OPEN isolated Chromium.
            var credential = await ScalarAsync(db, """
                UPDATE agent.runtime_generation_auth_sessions
                SET status='Finishing',updated_at_utc=now()
                WHERE generation=@generation AND status='Ready'
                  AND requested_at_utc>now()-interval '30 minutes'
                  AND actor_id=@actor
                  AND account_verified_at_utc > now()-interval '5 minutes'
                  AND account_username IS NOT NULL
                  AND account_customer_id IS NOT NULL
                RETURNING credential_id::text
                """, ct, ("generation", generation), ("actor", actor));
            if (credential is null || !Guid.TryParse(credential, out var credentialId))
                return Results.Conflict(new { code = "standby_auth_session_not_ready_or_wrong_operator" });
            if (!await DispatchAsync(token, "finish", generation, credentialId,
                "I_INDEPENDENTLY_VERIFIED_AUTHORIZED_PROVIDER_ACCESS", ct))
            {
                await UpdateStatusAsync(db, generation, "Finishing", "Ready", "github_dispatch_failed", ct);
                return Results.Conflict(new { code = "standby_authentication_dispatch_failed" });
            }
            return Results.Accepted("/api/agents/runtime-generations/authentication/",
                new { generation, status = "Finishing", activeGenerationUnchanged = true });
        }).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);
    }

    private static bool ValidTarget(string value)
        => value is { Length: >= 5 and <= 64 }
            && value.StartsWith("prod", StringComparison.Ordinal)
            && value[4] is >= '1' and <= '9'
            && value.Skip(5).All(c => c is >= '0' and <= '9')
            && value != "prod1"; // prod1 uses its preexisting session flow.

    private static async Task<bool> DispatchAsync(
        string token, string operation, string generation, Guid credential, string confirmation,
        CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var message = new HttpRequestMessage(HttpMethod.Post,
                "https://api.github.com/repos/CustomCodeCR/DholeAgentService/actions/workflows/standby-authentication.yml/dispatches");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            message.Headers.UserAgent.ParseAdd("DholeAgent-standby-authorization/1.0");
            message.Content = JsonContent.Create(new
            {
                @ref = "master",
                inputs = new
                {
                    operation, generation,
                    credential_id = credential.ToString("N"),
                    confirmation
                }
            });
            using var response = await http.SendAsync(message, ct);
            return response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Created;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task ExpireAsync(ServiceDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(Schema, ct);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE agent.runtime_generation_auth_sessions
            SET status='Expired', diagnostic_code='operator_session_expired',
                updated_at_utc=now()
            WHERE status IN ('Requested','Preparing','Ready')
              AND requested_at_utc < now() - interval '30 minutes'
            """, ct);
    }

    private static async Task UpdateStatusAsync(ServiceDbContext db, string generation,
        string expected, string newStatus, string code, CancellationToken ct)
    {
        _ = await ScalarAsync(db, """
            UPDATE agent.runtime_generation_auth_sessions
            SET status=@newStatus,diagnostic_code=@code,updated_at_utc=now()
            WHERE generation=@generation AND status=@expected
            RETURNING generation
            """, ct, ("generation", generation), ("expected", expected),
                ("newStatus", newStatus), ("code", code));
    }

    private static async Task<string?> ScalarAsync(ServiceDbContext db,
        string sql, CancellationToken ct, params (string Key, object Value)[] args)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = Command(db, sql, args);
            return (await cmd.ExecuteScalarAsync(ct))?.ToString();
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task<List<string[]>> ReadRowsAsync(ServiceDbContext db, string sql, CancellationToken ct)
    {
        var results = new List<string[]>();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = Command(db, sql);
            await using var rows = await cmd.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
            {
                var cols = new string[rows.FieldCount];
                for (var i = 0; i < cols.Length; i++)
                    cols[i] = rows.IsDBNull(i) ? "" : rows.GetValue(i).ToString() ?? "";
                results.Add(cols);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
        return results;
    }

    private static DbCommand Command(ServiceDbContext db, string sql,
        params (string Key, object Value)[] args)
    {
        var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in args)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = key;
            param.Value = value;
            cmd.Parameters.Add(param);
        }
        return cmd;
    }
}
