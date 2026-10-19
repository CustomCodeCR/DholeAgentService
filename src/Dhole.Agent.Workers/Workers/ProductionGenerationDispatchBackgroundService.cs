using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.Workers.Workers;

/// <summary>
/// PostgreSQL is authoritative for generation requests; GitHub Actions runs the
/// protected on-host executor. Never modifies Chromium state or provider circuits.
/// Provider-authentication challenges are deliberately NOT orchestration events.
/// </summary>
public sealed class ProductionGenerationDispatchBackgroundService(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<ProductionGenerationDispatchBackgroundService> logger) : BackgroundService
{
    // Shared with the operator-only readiness workflow.
    // Never create provider authorization automatically in the Worker.
    private const string ReadinessSchema = """
        CREATE TABLE IF NOT EXISTS agent.runtime_generation_readiness (
            generation text PRIMARY KEY,
            ready boolean NOT NULL DEFAULT false,
            provider_authorized boolean NOT NULL DEFAULT false,
            verified_by text NOT NULL,
            verified_at_utc timestamptz NOT NULL
        );
        """;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly Guid SchedulerActor = Guid.Empty;
    private static readonly string[] TechnicalCodes =
        ["maersk_chromium_crashed", "maersk_chromium_launch_failed", "maersk_worker_connection_failed",
         "maersk_profile_lock_unavailable", "maersk_infrastructure_transient"];
    private readonly HttpClient _github = new() { Timeout = TimeSpan.FromSeconds(10) };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("GenerationOrchestrator:Enabled"))
        {
            logger.LogInformation("GENERATION_DISPATCHER_DISABLED");
            return;
        }
        logger.LogInformation("GENERATION_DISPATCHER_RUNNING");
        var readinessSchemaInitialized = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
                    if (await ExistsAsync(db, stoppingToken))
                    {
                        if (!readinessSchemaInitialized)
                        {
                            await db.Database.ExecuteSqlRawAsync(ReadinessSchema, stoppingToken);
                            readinessSchemaInitialized = true;
                        }
                        // Close old/unfulfillable requests first instead of leaving the
                        // UI at Pending indefinitely or dispatching stale handoffs.
                        await ReconcilePendingAsync(db, stoppingToken);
                        // Manual requests must not depend on optional rule evaluation.
                        await DispatchPendingAsync(db, stoppingToken);
                        await DispatchProvisioningAsync(db, stoppingToken);
                        await ExpireOverdueAsync(db, stoppingToken);
                        try
                        {
                            await EvaluateRulesAsync(db, stoppingToken);
                        }
                        catch (Exception ruleError) when (ruleError is not OperationCanceledException)
                        {
                            logger.LogError(ruleError, "GENERATION_RULE_EVALUATION_FAILED");
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "GENERATION_DISPATCH_CYCLE_FAILED");
                }
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _github.Dispose(); }
    }

    private static async Task ReconcilePendingAsync(ServiceDbContext db, CancellationToken ct)
    {
        // Fail closed if an operator selected a merely registered environment.
        await QueryAsync(db, """
            UPDATE agent.runtime_generation_requests r
            SET status='Rejected', diagnostic_code='destination_not_provisioned',
                completed_at_utc=now()
            WHERE r.status='Pending' AND NOT EXISTS (
                SELECT 1 FROM agent.runtime_generation_provisions p
                WHERE p.generation=r.target_generation AND p.status='Provisioned'
            )
            RETURNING id::text
            """, ct);

        // The host-local attestation remains authoritative at handoff.
        // Reject unverified standbys before creating futile GitHub Actions runs.
        await QueryAsync(db, """
            UPDATE agent.runtime_generation_requests r
            SET status='Rejected',
                diagnostic_code=CASE
                    WHEN NOT EXISTS (
                        SELECT 1 FROM agent.runtime_generation_readiness a
                        WHERE a.generation=r.target_generation
                    ) THEN 'standby_readiness_missing'
                    WHEN EXISTS (
                        SELECT 1 FROM agent.runtime_generation_readiness a
                        WHERE a.generation=r.target_generation
                          AND (NOT a.ready OR NOT a.provider_authorized
                               OR length(trim(a.verified_by)) < 3)
                    ) THEN 'standby_authorization_unverified'
                    ELSE 'standby_readiness_expired'
                END,
                completed_at_utc=now()
            WHERE r.status='Pending'
              AND NOT EXISTS (
                SELECT 1 FROM agent.runtime_generation_readiness a
                WHERE a.generation=r.target_generation
                  AND a.ready AND a.provider_authorized
                  AND length(trim(a.verified_by)) >= 3
                  AND a.verified_at_utc <= now()
                  AND a.verified_at_utc > now() - interval '60 minutes'
              )
            RETURNING id::text
            """, ct);

        // A request older than the overall five-minute target is no longer
        // safe to activate automatically, even if it later becomes ready.
        await QueryAsync(db, """
            UPDATE agent.runtime_generation_requests
            SET status='Failed', diagnostic_code='manual_request_expired',
                completed_at_utc=now()
            WHERE status='Pending' AND created_at_utc < now() - interval '4 minutes 45 seconds'
            RETURNING id::text
            """, ct);
    }

    private static async Task<bool> ExistsAsync(ServiceDbContext db, CancellationToken ct)
    {
        return string.Equals(await QueryAsync(db,
            "SELECT to_regclass('agent.runtime_generation_requests') IS NOT NULL", ct), "True",
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ExpireOverdueAsync(ServiceDbContext db, CancellationToken ct)
    {
        // Fail closed: if Actions has not begun on time, later execution must
        // check status and refuse rather than performing a stale switch.
        await QueryAsync(db, """
            UPDATE agent.runtime_generation_requests
            SET status='Failed', diagnostic_code='github_dispatch_deadline_exceeded',
                completed_at_utc=now()
            WHERE status='Processing' AND diagnostic_code='github_workflow_dispatched'
              AND created_at_utc < now() - interval '4 minutes 45 seconds'
            RETURNING id::text
            """, ct);
    }

    private static async Task EvaluateRulesAsync(ServiceDbContext db, CancellationToken ct)
    {
        // A closed provider circuit is mandatory. All authenticated access
        // challenges remain with the provider's circuit breaker, never the switcher.
        var authSafe = await QueryAsync(db, """
            SELECT NOT EXISTS (
                SELECT 1 FROM agent.maersk_circuits
                WHERE state <> 'Closed' OR requires_operator
            )
            """, ct);
        if (!string.Equals(authSafe, "True", StringComparison.OrdinalIgnoreCase)) return;
        var active = await QueryAsync(db, """
            SELECT active_generation FROM agent.runtime_generation_state WHERE singleton=true
            """, ct);
        if (string.IsNullOrEmpty(active)) return;
        var destination = await QueryAsync(db, """
            SELECT g.name FROM agent.runtime_generations g
            INNER JOIN agent.runtime_generation_provisions p
                ON p.generation=g.name AND p.status='Provisioned'
            INNER JOIN agent.runtime_generation_readiness a
                ON a.generation=g.name AND a.ready AND a.provider_authorized
               AND length(trim(a.verified_by)) >= 3
               AND a.verified_at_utc <= now()
               AND a.verified_at_utc > now() - interval '60 minutes'
            WHERE g.name <> @active AND g.name ~ '^prod[1-9][0-9]*$'
            -- Choose the first ready numbered generation after the active one.
            -- Wrap to the lowest eligible number only when no higher one exists.
            -- Provider authorization and closed-circuit guards above are mandatory.
            ORDER BY CASE
                WHEN substring(g.name from 5)::numeric >
                    CASE WHEN @active ~ '^prod[1-9][0-9]*
            """, ct, ("active", active));
        if (string.IsNullOrEmpty(destination)) return;

        var cooldown = await QueryAsync(db, """
            SELECT updated_at_utc <= now() - interval '15 minutes'
            FROM agent.runtime_generation_state WHERE singleton=true
            """, ct);
        if (!string.Equals(cooldown, "True", StringComparison.OrdinalIgnoreCase)) return;

        // Avoid repeated GitHub Actions requests when a destination fails
        // readiness or an operator must investigate an infrastructure issue.
        var recentlyAttempted = await QueryAsync(db, """
            SELECT EXISTS(
                SELECT 1 FROM agent.runtime_generation_requests
                WHERE trigger_code <> 'manual'
                  AND created_at_utc > now() - interval '15 minutes'
            )
            """, ct);
        if (string.Equals(recentlyAttempted, "True", StringComparison.OrdinalIgnoreCase))
            return;

        // Provider errors and CAPTCHA are excluded from every technical trigger.
        var eligible = await QueryRowsAsync(db, """
            SELECT code, threshold::text, schedule_minutes::text
            FROM agent.runtime_generation_rules
            WHERE enabled=true AND code IN ('scheduled','technical_failure','data_quality')
            ORDER BY code
            """, ct);
        foreach (var rule in eligible)
        {
            var code = rule[0];
            var threshold = int.Parse(rule[1], System.Globalization.CultureInfo.InvariantCulture);
            var minutes = int.Parse(rule[2], System.Globalization.CultureInfo.InvariantCulture);
            bool due = code switch
            {
                "scheduled" => string.Equals(await QueryAsync(db, """
                    SELECT updated_at_utc <= now() - make_interval(mins => @minutes)
                    FROM agent.runtime_generation_state WHERE singleton=true
                    """, ct, ("minutes", minutes)), "True", StringComparison.OrdinalIgnoreCase),
                "technical_failure" => await CountTechnicalAsync(db, minutes, ct) >= threshold,
                // Failed output/partial extraction, NOT authentication challenge.
                "data_quality" => int.TryParse(await QueryAsync(db, """
                    SELECT COUNT(*)::text FROM agent."AgentExecutions"
                    WHERE status='PartiallyCompleted' AND completed_at >= now() - make_interval(mins => @minutes)
                    """, ct, ("minutes", minutes)), out var count) && count >= threshold,
                _ => false,
            };
            if (!due) continue;
            await QueryAsync(db, """
                INSERT INTO agent.runtime_generation_requests
                  (id,target_generation,trigger_code,reason,actor_id)
                VALUES (gen_random_uuid(), @target, @code, @reason, @actor)
                ON CONFLICT DO NOTHING RETURNING id::text
                """, ct, ("target", destination), ("code", code),
                ("reason", "Automatic orchestration: verified " + code + " criterion"),
                ("actor", SchedulerActor));
            break; // one transition per evaluation
        }
    }

    private static async Task<int> CountTechnicalAsync(ServiceDbContext db, int minutes, CancellationToken ct)
    {
        var result = await QueryAsync(db, """
            SELECT COUNT(*)::text FROM agent."AgentExecutions"
            WHERE status='Failed' AND completed_at >= now() - make_interval(mins => @minutes)
              AND error_code = ANY(@codes)
            """, ct, ("minutes", minutes), ("codes", TechnicalCodes));
        return int.TryParse(result, out var parsed) ? parsed : 0;
    }

    private async Task DispatchProvisioningAsync(ServiceDbContext db, CancellationToken ct)
    {
        var pending = await QueryRowsAsync(db, """
            WITH candidate AS (
              SELECT generation FROM agent.runtime_generation_provisions
              WHERE status='Pending' ORDER BY created_at_utc
              LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE agent.runtime_generation_provisions r
            SET status='Dispatching', updated_at_utc=now()
            FROM candidate WHERE r.generation=candidate.generation
            RETURNING r.generation
            """, ct);
        if (pending.Count == 0) return;
        var generation = pending[0][0];
        // Registering an environment through the UI provisions its isolated
        // infrastructure once; it does NOT switch Workers or authenticate.
        if (!System.Text.RegularExpressions.Regex.IsMatch(generation, @"^prod[1-9][0-9]*$") ||
            generation.Length > 64)
        {
            await SetProvisionStatusAsync(db, generation, "Failed", "invalid_generation", ct);
            return;
        }
        var token = configuration["GenerationOrchestrator:GitHubToken"];
        if (string.IsNullOrWhiteSpace(token))
        {
            await SetProvisionStatusAsync(db, generation, "Failed", "github_actions_token_missing", ct);
            return;
        }
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post,
                "https://api.github.com/repos/CustomCodeCR/DholeAgentService/actions/workflows/provision-agent-prod1.yml/dispatches");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            message.Headers.UserAgent.ParseAdd("DholeAgent-generation-orchestrator/1.0");
            message.Content = JsonContent.Create(new
            {
                @ref = "master",
                inputs = new {
                    generation,
                    mode = "apply",
                    confirmation = "PROVISION_INDEPENDENT_GENERATION"
                }
            });
            using var response = await _github.SendAsync(message, ct);
            if (response.StatusCode is not HttpStatusCode.NoContent and not HttpStatusCode.Created)
                throw new InvalidOperationException("GitHub provisioning dispatch refused");
            await SetProvisionStatusAsync(db, generation, "Dispatched", "github_provision_requested", ct);
            logger.LogInformation("GENERATION_PROVISION_DISPATCHED generation={Generation}", generation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("GENERATION_PROVISION_DISPATCH_FAILED generation={Generation} reason={Type}",
                generation, exception.GetType().Name);
            await SetProvisionStatusAsync(db, generation, "Failed", "github_provision_dispatch_failed", ct);
        }
    }

    private static Task SetProvisionStatusAsync(ServiceDbContext db, string generation,
        string status, string code, CancellationToken ct) => QueryAsync(db, """
            UPDATE agent.runtime_generation_provisions
            SET status=@status, diagnostic_code=@code, updated_at_utc=now()
            WHERE generation=@generation AND status IN ('Dispatching','Dispatched')
            RETURNING generation
            """, ct, ("status", status), ("code", code), ("generation", generation));

    private async Task DispatchPendingAsync(ServiceDbContext db, CancellationToken ct)
    {
        // Atomic claim, safe across multiple worker replicas.
        var fields = await QueryRowsAsync(db, """
            WITH candidate AS (
              SELECT id FROM agent.runtime_generation_requests
              WHERE status='Pending' ORDER BY created_at_utc, id
              LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE agent.runtime_generation_requests request
            SET status='Processing', diagnostic_code='github_workflow_dispatched'
            FROM candidate WHERE request.id=candidate.id
            RETURNING request.id::text,request.target_generation,request.trigger_code
            """, ct);
        if (fields.Count == 0) return;
        var id = fields[0][0];
        var target = fields[0][1];
        var trigger = fields[0][2];
        var token = configuration["GenerationOrchestrator:GitHubToken"];
        if (string.IsNullOrWhiteSpace(token))
        {
            await SetStatusAsync(db, id, "Failed", "github_actions_token_missing", ct);
            logger.LogWarning("GENERATION_GITHUB_TOKEN_MISSING request={RequestId}", id);
            return;
        }

        try
        {
            var branch = configuration["GenerationOrchestrator:ActionsRef"] ?? "master";
            if (branch != "master") throw new InvalidOperationException("Only protected master workflow is permitted.");
            var repository = "CustomCodeCR/DholeAgentService";
            using var message = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.github.com/repos/{repository}/actions/workflows/production-generation-apply.yml/dispatches");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            message.Headers.UserAgent.ParseAdd("DholeAgent-generation-orchestrator/1.0");
            message.Content = JsonContent.Create(new
            {
                @ref = branch,
                inputs = new { request_id = id }
            });
            using var response = await _github.SendAsync(message, ct);
            if (response.StatusCode is not HttpStatusCode.NoContent and not HttpStatusCode.Created)
                throw new InvalidOperationException("GitHub workflow_dispatch was rejected (HTTP " + (int)response.StatusCode + ")");
            await SetStatusAsync(db, id, "Processing", "github_workflow_dispatched", ct);
            logger.LogInformation("GENERATION_GITHUB_DISPATCHED request={RequestId} target={Target} trigger={Trigger}",
                id, target, trigger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError("GENERATION_GITHUB_DISPATCH_FAILED request={RequestId} reason={Reason}", id, ex.GetType().Name);
            await SetStatusAsync(db, id, "Failed", "github_dispatch_failed", ct);
        }
    }

    private static Task SetStatusAsync(ServiceDbContext db, string id, string status, string reason, CancellationToken ct) =>
        QueryAsync(db, """
            UPDATE agent.runtime_generation_requests
            SET status=@status, diagnostic_code=@reason,
                completed_at_utc=CASE WHEN @status='Failed' THEN now() ELSE completed_at_utc END
            WHERE id=@id::uuid AND status='Processing'
            RETURNING id::text
            """, ct, ("status", status), ("reason", reason), ("id", id));

    private static async Task<string?> QueryAsync(ServiceDbContext db, string sql,
        CancellationToken ct, params (string, object)[] args)
    {
        var rows = await QueryRowsAsync(db, sql, ct, args);
        return rows.Count == 0 ? null : rows[0][0];
    }

    private static async Task<List<string[]>> QueryRowsAsync(ServiceDbContext db, string sql,
        CancellationToken ct, params (string, object)[] args)
    {
        var result = new List<string[]>();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in args)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new string[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
                result.Add(row);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
        return result;
    }
}
