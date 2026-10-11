using System.Data.Common;
using CustomCodeFramework.Api.Responses;
using Dhole.Agent.Api.Authorization;
using Dhole.Agent.Api.Extensions;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.Api.Endpoints;

/// <summary>
/// Authorised control-plane for the production Worker generation.
/// Durable state lives in PostgreSQL; no container or browser credentials
/// ever cross the API. An event request is NOT a successful handoff.
/// </summary>
internal static class ProductionGenerationEndpoints
{
    private static readonly string[] KnownTriggers =
        ["manual", "scheduled", "technical_failure", "data_quality"];

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS agent.runtime_generation_state (
            singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
            active_generation text NOT NULL DEFAULT 'prod',
            updated_at_utc timestamptz NOT NULL DEFAULT now()
        );
        INSERT INTO agent.runtime_generation_state(singleton,active_generation)
        VALUES (true,'prod') ON CONFLICT (singleton) DO NOTHING;

        CREATE TABLE IF NOT EXISTS agent.runtime_generations (
            name text PRIMARY KEY CHECK (name = 'prod' OR name ~ '^prod[1-9][0-9]*
            code text PRIMARY KEY,
            enabled boolean NOT NULL DEFAULT false,
            threshold integer NOT NULL DEFAULT 1 CHECK (threshold BETWEEN 1 AND 50),
            schedule_minutes integer NOT NULL DEFAULT 60
                CHECK (schedule_minutes BETWEEN 15 AND 10080),
            updated_at_utc timestamptz NOT NULL DEFAULT now()
        );
        INSERT INTO agent.runtime_generation_rules(code,enabled)
        VALUES ('manual',true),('scheduled',false),
               ('technical_failure',false),('data_quality',false)
        ON CONFLICT (code) DO NOTHING;

        CREATE TABLE IF NOT EXISTS agent.runtime_generation_requests (
            id uuid PRIMARY KEY,
            target_generation text NOT NULL,
            trigger_code text NOT NULL,
            reason text NOT NULL,
            actor_id uuid NOT NULL,
            status text NOT NULL DEFAULT 'Pending'
                CHECK (status IN ('Pending','Processing','Completed','Rejected','Failed')),
            created_at_utc timestamptz NOT NULL DEFAULT now(),
            completed_at_utc timestamptz NULL,
            diagnostic_code text NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_runtime_generation_pending
            ON agent.runtime_generation_requests((true))
            WHERE status IN ('Pending','Processing');
        """;

    internal sealed record RegisterGeneration(string Name);
    internal sealed record RequestGeneration(string TargetGeneration, string Trigger, string Reason);
    internal sealed record UpdateRule(string Code, bool Enabled, int Threshold, int ScheduleMinutes);

    public static void MapProductionGenerationEndpoints(this RouteGroupBuilder root)
    {
        var group = root.MapGroup("/runtime-generations");
        group.MapGet("/", async (ServiceDbContext db, CancellationToken ct) =>
        {
            await EnsureSchemaAsync(db, ct);
            var active = await ScalarAsync(db,
                "SELECT active_generation FROM agent.runtime_generation_state WHERE singleton=true", ct);
            var generationRows = await RowsAsync(db, """
                SELECT name, registered_at_utc::text
                FROM agent.runtime_generations
                ORDER BY CASE WHEN name='prod' THEN 0 ELSE 1 END,
                         CASE WHEN name='prod' THEN 0 ELSE substring(name from 5)::numeric END
                """, ct);
            var rules = await RowsAsync(db, """
                SELECT code, enabled::text, threshold::text, schedule_minutes::text
                FROM agent.runtime_generation_rules ORDER BY code
                """, ct);
            var events = await RowsAsync(db, """
                SELECT id::text, target_generation, trigger_code, reason,
                       status, created_at_utc::text,
                       coalesce(completed_at_utc::text,''), coalesce(diagnostic_code,'')
                FROM agent.runtime_generation_requests
                ORDER BY created_at_utc DESC LIMIT 50
                """, ct);
            var result = new
            {
                activeGeneration = active ?? "unknown",
                generations = generationRows.Select(r => new {
                    name = r[0], browserState = "IsolatedPersistent", sharedDatabases = true,
                    registeredAtUtc = r[1]
                }),
                rules = rules.Select(r => new {
                    code = r[0], enabled = r[1] == "true", threshold = int.Parse(r[2]),
                    scheduleMinutes = int.Parse(r[3])
                }),
                events = events.Select(r => new {
                    id = r[0], targetGeneration = r[1], trigger = r[2], reason = r[3],
                    status = r[4], createdAtUtc = r[5], completedAtUtc = r[6], diagnosticCode = r[7]
                })
            };
            return Results.Ok(ApiResponse<object>.Ok(result));
        })
        .RequireScope(AgentScopeNames.ExecutionsView)
        .RequireScope(AgentScopeNames.BrowserProfilesView);

        group.MapPost("/register", async (
            RegisterGeneration request, HttpContext context, ServiceDbContext db, CancellationToken ct) =>
        {
            if (context.GetCurrentUserId() is not { } actor || actor == Guid.Empty)
                return Results.Unauthorized();
            if (!ValidGenerationName(request.Name) || request.Name == "prod")
                return Results.BadRequest(new { error = "Generation must be prod followed by a positive integer." });
            await EnsureSchemaAsync(db, ct);
            try
            {
                await ExecuteAsync(db, """
                    INSERT INTO agent.runtime_generations (name)
                    VALUES (@name) ON CONFLICT(name) DO NOTHING
                    """, ct, ("name", request.Name));
            }
            catch (DbException)
            {
                return Results.Conflict(new { error = "Generation registration was rejected." });
            }
            // Registration is metadata only: Docker, Chromium and Workers remain untouched.
            return Results.Ok(new { name = request.Name, registered = true, provisioned = false });
        }).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);

        group.MapPost("/requests", async (
            RequestGeneration request, HttpContext context, ServiceDbContext db, CancellationToken ct) =>
        {
            var actor = context.GetCurrentUserId();
            if (actor is null || actor.Value == Guid.Empty)
                return Results.Unauthorized();
            if (!ValidGenerationName(request.TargetGeneration) ||
                !KnownTriggers.Contains(request.Trigger) ||
                string.IsNullOrWhiteSpace(request.Reason) ||
                request.Reason.Trim().Length is < 12 or > 500)
                return Results.BadRequest(new { error = "Invalid target, event type, or operational reason." });
            await EnsureSchemaAsync(db, ct);
            var active = await ScalarAsync(db,
                "SELECT active_generation FROM agent.runtime_generation_state WHERE singleton=true", ct);
            if (active == request.TargetGeneration)
                return Results.Conflict(new { error = "Target generation is already active." });
            var registered = await ScalarAsync(db,
                "SELECT name FROM agent.runtime_generations WHERE name=@name", ct,
                ("name", request.TargetGeneration));
            if (registered is null)
                return Results.Conflict(new { error = "Destination generation must be registered first." });
            var ruleEnabled = await ScalarAsync(db,
                "SELECT enabled::text FROM agent.runtime_generation_rules WHERE code=@code", ct,
                ("code", request.Trigger));
            if (ruleEnabled != "true")
                return Results.Conflict(new { error = "This event type is disabled by the orchestrator rule." });
            // No provider auth challenge or session repair is accepted as a trigger.
            var eventId = Guid.NewGuid();
            try
            {
                await ExecuteAsync(db, """
                    INSERT INTO agent.runtime_generation_requests
                        (id,target_generation,trigger_code,reason,actor_id)
                    VALUES (@id,@target,@trigger,@reason,@actor)
                    """, ct, ("id", eventId), ("target", request.TargetGeneration),
                    ("trigger", request.Trigger), ("reason", request.Reason.Trim()),
                    ("actor", actor!.Value));
            }
            catch (Exception ex) when (ex is DbException)
            {
                return Results.Conflict(new { error = "Another generation transition is already pending." });
            }
            return Results.Accepted($"/api/agents/runtime-generations/requests/{eventId}",
                new { id = eventId, status = "Pending", executed = false });
        }).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);

        group.MapPut("/rules/{code}", async (
            string code, UpdateRule rule, HttpContext context, ServiceDbContext db, CancellationToken ct) =>
        {
            if (context.GetCurrentUserId() is not { } actor || actor == Guid.Empty)
                return Results.Unauthorized();
            if (!KnownTriggers.Contains(code) || rule.Code != code ||
                rule.Threshold is < 1 or > 50 || rule.ScheduleMinutes is < 15 or > 10080)
                return Results.BadRequest(new { error = "Invalid generation rule." });
            await EnsureSchemaAsync(db, ct);
            await ExecuteAsync(db, """
                UPDATE agent.runtime_generation_rules
                SET enabled=@enabled, threshold=@threshold,
                    schedule_minutes=@minutes, updated_at_utc=now()
                WHERE code=@code
                """, ct, ("enabled", rule.Enabled), ("threshold", rule.Threshold),
                ("minutes", rule.ScheduleMinutes), ("code", code));
            return Results.Ok(new { saved = true, code });
        }).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);
    }

    private static bool ValidGenerationName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
        if (value == "prod") return true;
        if (!value.StartsWith("prod", StringComparison.Ordinal) || value.Length < 5 || value[4] is < '1' or > '9')
            return false;
        return value.AsSpan(5).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static async Task EnsureSchemaAsync(ServiceDbContext db, CancellationToken ct)
        => await db.Database.ExecuteSqlRawAsync(Schema, ct);

    private static DbCommand Command(ServiceDbContext db, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name; p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static async Task<string?> ScalarAsync(
        ServiceDbContext db, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = Command(db, sql, parameters);
            var value = await cmd.ExecuteScalarAsync(ct);
            return value?.ToString();
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task<List<string[]>> RowsAsync(ServiceDbContext db, string sql, CancellationToken ct)
    {
        var rows = new List<string[]>();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = Command(db, sql);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new string[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = reader.GetString(i);
                rows.Add(row);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
        return rows;
    }

    private static async Task ExecuteAsync(
        ServiceDbContext db, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = Command(db, sql, parameters);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
),
            registered_at_utc timestamptz NOT NULL DEFAULT now()
        );
        INSERT INTO agent.runtime_generations(name)
        VALUES ('prod'), ('prod1') ON CONFLICT (name) DO NOTHING;

        CREATE TABLE IF NOT EXISTS agent.runtime_generation_rules (
            code text PRIMARY KEY,
            enabled boolean NOT NULL DEFAULT false,
            threshold integer NOT NULL DEFAULT 1 CHECK (threshold BETWEEN 1 AND 50),
            schedule_minutes integer NOT NULL DEFAULT 60
                CHECK (schedule_minutes BETWEEN 15 AND 10080),
            updated_at_utc timestamptz NOT NULL DEFAULT now()
        );
        INSERT INTO agent.runtime_generation_rules(code,enabled)
        VALUES ('manual',true),('scheduled',false),
               ('technical_failure',false),('data_quality',false)
        ON CONFLICT (code) DO NOTHING;

        CREATE TABLE IF NOT EXISTS agent.runtime_generation_requests (
            id uuid PRIMARY KEY,
            target_generation text NOT NULL,
            trigger_code text NOT NULL,
            reason text NOT NULL,
            actor_id uuid NOT NULL,
            status text NOT NULL DEFAULT 'Pending'
                CHECK (status IN ('Pending','Processing','Completed','Rejected','Failed')),
            created_at_utc timestamptz NOT NULL DEFAULT now(),
            completed_at_utc timestamptz NULL,
            diagnostic_code text NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_runtime_generation_pending
            ON agent.runtime_generation_requests((true))
            WHERE status IN ('Pending','Processing');
        """;

    internal sealed record RequestGeneration(string TargetGeneration, string Trigger, string Reason);
    internal sealed record UpdateRule(string Code, bool Enabled, int Threshold, int ScheduleMinutes);

    public static void MapProductionGenerationEndpoints(this RouteGroupBuilder root)
    {
        var group = root.MapGroup("/runtime-generations");
        group.MapGet("/", async (ServiceDbContext db, CancellationToken ct) =>
        {
            await EnsureSchemaAsync(db, ct);
            var active = await ScalarAsync(db,
                "SELECT active_generation FROM agent.runtime_generation_state WHERE singleton=true", ct);
            var rules = await RowsAsync(db, """
                SELECT code, enabled::text, threshold::text, schedule_minutes::text
                FROM agent.runtime_generation_rules ORDER BY code
                """, ct);
            var events = await RowsAsync(db, """
                SELECT id::text, target_generation, trigger_code, reason,
                       status, created_at_utc::text,
                       coalesce(completed_at_utc::text,''), coalesce(diagnostic_code,'')
                FROM agent.runtime_generation_requests
                ORDER BY created_at_utc DESC LIMIT 50
                """, ct);
            var result = new
            {
                activeGeneration = active ?? "unknown",
                generations = new[]
                {
                    new { name = "prod", browserState = "IsolatedPersistent", sharedDatabases = true },
                    new { name = "prod1", browserState = "IsolatedPersistent", sharedDatabases = true }
                },
                rules = rules.Select(r => new {
                    code = r[0], enabled = r[1] == "true", threshold = int.Parse(r[2]),
                    scheduleMinutes = int.Parse(r[3])
                }),
                events = events.Select(r => new {
                    id = r[0], targetGeneration = r[1], trigger = r[2], reason = r[3],
                    status = r[4], createdAtUtc = r[5], completedAtUtc = r[6], diagnosticCode = r[7]
                })
            };
            return Results.Ok(ApiResponse<object>.Ok(result));
        })
        .RequireScope(AgentScopeNames.ExecutionsView)
        .RequireScope(AgentScopeNames.BrowserProfilesView);

        group.MapPost("/requests", async (
            RequestGeneration request, HttpContext context, ServiceDbContext db, CancellationToken ct) =>
        {
            var actor = context.GetCurrentUserId();
            if (actor is null || actor.Value == Guid.Empty)
                return Results.Unauthorized();
            if (request.TargetGeneration is not ("prod" or "prod1") ||
                !KnownTriggers.Contains(request.Trigger) ||
                string.IsNullOrWhiteSpace(request.Reason) ||
                request.Reason.Trim().Length is < 12 or > 500)
                return Results.BadRequest(new { error = "Invalid target, event type, or operational reason." });
            await EnsureSchemaAsync(db, ct);
            var active = await ScalarAsync(db,
                "SELECT active_generation FROM agent.runtime_generation_state WHERE singleton=true", ct);
            if (active == request.TargetGeneration)
                return Results.Conflict(new { error = "Target generation is already active." });
            var ruleEnabled = await ScalarAsync(db,
                "SELECT enabled::text FROM agent.runtime_generation_rules WHERE code=@code", ct,
                ("code", request.Trigger));
            if (ruleEnabled != "true")
                return Results.Conflict(new { error = "This event type is disabled by the orchestrator rule." });
            // No provider auth challenge or session repair is accepted as a trigger.
            var eventId = Guid.NewGuid();
            try
            {
                await ExecuteAsync(db, """
                    INSERT INTO agent.runtime_generation_requests
                        (id,target_generation,trigger_code,reason,actor_id)
                    VALUES (@id,@target,@trigger,@reason,@actor)
                    """, ct, ("id", eventId), ("target", request.TargetGeneration),
                    ("trigger", request.Trigger), ("reason", request.Reason.Trim()),
                    ("actor", actor!.Value));
            }
            catch (Exception ex) when (ex is DbException)
            {
                return Results.Conflict(new { error = "Another generation transition is already pending." });
            }
            return Results.Accepted($"/api/agents/runtime-generations/requests/{eventId}",
                new { id = eventId, status = "Pending", executed = false });
        }).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);

        group.MapPut("/rules/{code}", async (
            string code, UpdateRule rule, HttpContext context, ServiceDbContext db, CancellationToken ct) =>
        {
            if (context.GetCurrentUserId() is not { } actor || actor == Guid.Empty)
                return Results.Unauthorized();
            if (!KnownTriggers.Contains(code) || rule.Code != code ||
                rule.Threshold is < 1 or > 50 || rule.ScheduleMinutes is < 15 or > 10080)
                return Results.BadRequest(new { error = "Invalid generation rule." });
            await EnsureSchemaAsync(db, ct);
            await ExecuteAsync(db, """
                UPDATE agent.runtime_generation_rules
                SET enabled=@enabled, threshold=@threshold,
                    schedule_minutes=@minutes, updated_at_utc=now()
                WHERE code=@code
                """, ct, ("enabled", rule.Enabled), ("threshold", rule.Threshold),
                ("minutes", rule.ScheduleMinutes), ("code", code));
            return Results.Ok(new { saved = true, code });
        }).RequireScope(AgentScopeNames.BrowserProfilesAuthenticate);
    }

    private static async Task EnsureSchemaAsync(ServiceDbContext db, CancellationToken ct)
        => await db.Database.ExecuteSqlRawAsync(Schema, ct);

    private static DbCommand Command(ServiceDbContext db, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name; p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static async Task<string?> ScalarAsync(
        ServiceDbContext db, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = Command(db, sql, parameters);
            var value = await cmd.ExecuteScalarAsync(ct);
            return value?.ToString();
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task<List<string[]>> RowsAsync(ServiceDbContext db, string sql, CancellationToken ct)
    {
        var rows = new List<string[]>();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = Command(db, sql);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new string[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = reader.GetString(i);
                rows.Add(row);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
        return rows;
    }

    private static async Task ExecuteAsync(
        ServiceDbContext db, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = Command(db, sql, parameters);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
