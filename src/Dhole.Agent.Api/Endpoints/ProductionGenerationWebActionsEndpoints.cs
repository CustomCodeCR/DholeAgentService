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
/// Scoped web control for the protected GitHub Actions generation workflow.
/// Every request is durable; the runner independently validates identity,
/// readiness, circuits, infrastructure and worker ownership before acting.
/// </summary>
internal static class ProductionGenerationWebActionsEndpoints
{
    private const string TableSql = """
        CREATE TABLE IF NOT EXISTS agent.runtime_generation_web_actions (
            id uuid PRIMARY KEY,
            generation text NOT NULL,
            operation text NOT NULL CHECK (operation IN
                ('inspect','reconcile','provision-check','provision','attest',
                 'preflight','switch','technical-recovery')),
            actor_id uuid NOT NULL,
            confirmed_provider_access boolean NOT NULL DEFAULT false,
            status text NOT NULL CHECK (status IN
                ('Pending','Dispatched','Running','Completed','Rejected','Failed')),
            diagnostic_code text,
            created_at_utc timestamptz NOT NULL DEFAULT now(),
            updated_at_utc timestamptz NOT NULL DEFAULT now(),
            completed_at_utc timestamptz
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_generation_web_action_active
        ON agent.runtime_generation_web_actions((true))
        WHERE status IN ('Pending','Dispatched','Running');
        """;

    internal sealed record ActionRequest(
        string Operation, string Generation, bool ConfirmedProviderAccess = false,
        bool ConfirmedHandoff = false);

    public static void MapWebActions(this RouteGroupBuilder group)
    {
        var actions = group.MapGroup("/actions");

        actions.MapGet("/", async (ServiceDbContext db, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlRawAsync(TableSql, ct);
            var rows = await FetchRowsAsync(db, """
                SELECT id::text, generation, operation, status,
                       coalesce(diagnostic_code,''), created_at_utc::text,
                       coalesce(completed_at_utc::text,'')
                FROM agent.runtime_generation_web_actions
                ORDER BY created_at_utc DESC LIMIT 40
                """, ct);
            return Results.Ok(ApiResponse<object>.Ok(rows.Select(r => new {
                id = r[0], generation = r[1], operation = r[2], status = r[3],
                diagnosticCode = r[4], createdAtUtc = r[5], completedAtUtc = r[6]
            }).ToArray()));
        })
        .RequireScope(AgentScopeNames.ExecutionsView)
        .RequireScope(AgentScopeNames.BrowserProfilesView);

        actions.MapPost("/", async (
            ActionRequest request, HttpContext context, ServiceDbContext db,
            IConfiguration configuration, CancellationToken ct) =>
        {
            if (context.GetCurrentUserId() is not { } actor || actor == Guid.Empty)
                return Results.Unauthorized();

            var allowed = request.Operation is
                "inspect" or "reconcile" or "provision-check" or "provision" or
                "attest" or "preflight" or "switch" or "technical-recovery";
            if (!allowed || !ValidGeneration(request.Generation))
                return Results.BadRequest(new { code = "invalid_generation_action" });

            if (request.Operation == "attest" && !request.ConfirmedProviderAccess)
                return Results.BadRequest(new { code = "independent_provider_confirmation_required" });
            if (request.Operation == "switch" && !request.ConfirmedHandoff)
                return Results.BadRequest(new { code = "explicit_handoff_confirmation_required" });
            if (request.Operation is "switch" or "attest" or "provision"
                && request.Generation == "prod")
                return Results.BadRequest(new { code = "numbered_standby_required" });

            var token = configuration["GenerationOrchestrator:GitHubToken"];
            if (!configuration.GetValue<bool>("GenerationOrchestrator:Enabled")
                || string.IsNullOrWhiteSpace(token))
                return Results.Conflict(new { code = "github_dispatcher_not_configured" });

            await db.Database.ExecuteSqlRawAsync(TableSql, ct);
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE agent.runtime_generation_web_actions
                SET status='Failed', diagnostic_code='workflow_request_expired',
                    updated_at_utc=now(),completed_at_utc=now()
                WHERE status IN ('Pending','Dispatched')
                  AND created_at_utc < now()-interval '30 minutes';
                """, ct);

            var active = await ScalarAsync(db,
                "SELECT active_generation FROM agent.runtime_generation_state WHERE singleton=true", ct);
            if (request.Operation is "provision" or "provision-check" or "attest" or "preflight" or "switch"
                && active == request.Generation)
                return Results.Conflict(new { code = "generation_already_active" });

            if (request.Operation is "provision" or "provision-check" or "attest" or "preflight" or "switch")
            {
                var provision = await ScalarAsync(db, """
                    SELECT coalesce(p.status,'Registered')
                    FROM agent.runtime_generations g
                    LEFT JOIN agent.runtime_generation_provisions p ON p.generation=g.name
                    WHERE g.name=@generation
                    """, ct, ("generation", request.Generation));
                if (provision is null)
                    return Results.Conflict(new { code = "generation_not_registered" });
                if (request.Operation is "attest" or "preflight" or "switch"
                    && provision != "Provisioned")
                    return Results.Conflict(new { code = "destination_not_provisioned" });
            }

            if (request.Operation is "attest" or "switch")
            {
                // Provider-wide restrictions are shared between generations.
                // No browser/worker rotation may clear a provider circuit.
                var clear = await ScalarAsync(db, """
                    SELECT NOT EXISTS (SELECT 1 FROM agent.maersk_circuits
                        WHERE state<>'Closed' OR requires_operator)
                    """, ct);
                if (clear != "True" && clear != "true")
                    return Results.Conflict(new { code = "provider_circuit_or_jobs_blocked" });
            }

            if (request.Operation == "attest")
            {
                // An operator checkbox is never enough. The live independent
                // Hub probe must have succeeded recently in this generation.
                var verified = await ScalarAsync(db, """
                    SELECT EXISTS (
                        SELECT 1 FROM agent.runtime_generation_auth_sessions
                        WHERE generation=@generation AND status='Verified'
                          AND account_verified_at_utc > now()-interval '5 minutes'
                          AND account_verified_at_utc <= now()
                    )
                    """, ct, ("generation", request.Generation));
                if (verified != "True" && verified != "true")
                    return Results.Conflict(new { code = "recent_verified_hub_required" });
            }

            if (request.Operation == "switch")
            {
                var permitted = await ScalarAsync(db, """
                    SELECT EXISTS (
                      SELECT 1 FROM agent.runtime_generation_readiness
                      WHERE generation=@generation AND ready AND provider_authorized
                        AND length(trim(verified_by))>=3
                        AND verified_at_utc>now()-interval '60 minutes'
                        AND verified_at_utc<=now()
                    ) AND EXISTS (
                      SELECT 1 FROM agent.runtime_generation_rules
                      WHERE code='manual' AND enabled
                    )
                    """, ct, ("generation", request.Generation));
                if (permitted != "True" && permitted != "true")
                    return Results.Conflict(new { code = "standby_authorization_or_manual_rule_missing" });
            }

            var id = Guid.NewGuid();
            try
            {
                await ExecuteAsync(db, """
                    INSERT INTO agent.runtime_generation_web_actions
                        (id,generation,operation,actor_id,confirmed_provider_access,status)
                    VALUES (@id,@generation,@operation,@actor,@confirmed,'Pending')
                    """, ct, ("id", id), ("generation", request.Generation),
                    ("operation", request.Operation), ("actor", actor),
                    ("confirmed", request.ConfirmedProviderAccess));
            }
            catch (DbException)
            {
                return Results.Conflict(new { code = "another_generation_action_in_progress" });
            }

            var success = await DispatchAsync(token, request.Operation,
                request.Generation, id, ct);
            if (!success)
            {
                await ExecuteAsync(db, """
                    UPDATE agent.runtime_generation_web_actions
                    SET status='Failed',diagnostic_code='github_dispatch_failed',
                        updated_at_utc=now(),completed_at_utc=now()
                    WHERE id=@id AND status='Pending'
                    """, ct, ("id", id));
                return Results.Conflict(new { code = "github_dispatch_failed" });
            }
            await ExecuteAsync(db, """
                UPDATE agent.runtime_generation_web_actions
                SET status='Dispatched',updated_at_utc=now()
                WHERE id=@id AND status='Pending'
                """, ct, ("id", id));
            return Results.Accepted("/api/agents/runtime-generations/actions/",
                new { id, generation = request.Generation, operation = request.Operation,
                    status = "Dispatched", executed = false });
        }).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);
    }

    private static bool ValidGeneration(string? name)
        => name is { Length: >= 4 and <= 64 }
           && (name == "prod" || (name.Length > 4
                && name.StartsWith("prod", StringComparison.Ordinal)
                && name[4] is >= '1' and <= '9'
                && name.Skip(5).All(char.IsAsciiDigit)));

    private static async Task<bool> DispatchAsync(string token, string operation,
        string generation, Guid id, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var message = new HttpRequestMessage(HttpMethod.Post,
                "https://api.github.com/repos/CustomCodeCR/DholeAgentService/actions/workflows/production-generation-actions.yml/dispatches");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            message.Headers.UserAgent.ParseAdd("DholeAgent-generation-web/1.0");
            message.Content = JsonContent.Create(new {
                @ref = "master",
                inputs = new {
                    operation, generation, action_request_id = id.ToString("D"),
                    confirmation = operation switch {
                        "provision" => "PROVISION_INDEPENDENT_GENERATION",
                        "switch" => "AUTHORIZE_VERIFIED_WORKER_HANDOFF",
                        _ => ""
                    }
                }
            });
            using var response = await http.SendAsync(message, ct);
            return response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Created;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static DbCommand MakeCommand(ServiceDbContext db, string sql,
        params (string Name, object Value)[] args)
    {
        var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in args)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = key;
            parameter.Value = value;
            cmd.Parameters.Add(parameter);
        }
        return cmd;
    }

    private static async Task<string?> ScalarAsync(ServiceDbContext db, string sql,
        CancellationToken ct, params (string Name, object Value)[] args)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = MakeCommand(db, sql, args);
            return (await command.ExecuteScalarAsync(ct))?.ToString();
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task ExecuteAsync(ServiceDbContext db, string sql,
        CancellationToken ct, params (string Name, object Value)[] args)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = MakeCommand(db, sql, args);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task<List<string[]>> FetchRowsAsync(ServiceDbContext db,
        string sql, CancellationToken ct)
    {
        var result = new List<string[]>();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = MakeCommand(db, sql);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new string[reader.FieldCount];
                for (var index = 0; index < row.Length; index++)
                    row[index] = reader.IsDBNull(index) ? "" : reader.GetValue(index).ToString() ?? "";
                result.Add(row);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
        return result;
    }
}
