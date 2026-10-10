using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Dhole.Agent.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.IntegrationTests;

[TestClass]
public sealed class MaerskPhase5ResumePostgresTests
{
    private static string? Connection()
    {
        var raw = Environment.GetEnvironmentVariable("AGENT_QUEUE_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.Inconclusive("Phase 5 resume requires PostgreSQL.");
            return null;
        }
        return new Npgsql.NpgsqlConnectionStringBuilder(raw)
        {
            Database = "agent_phase5_test_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
    }

    private static ServiceDbContext Db(string connection)
        => new(new DbContextOptionsBuilder<ServiceDbContext>().UseNpgsql(connection).Options);

    private static PostgresMaerskCircuitBreaker Circuit(ServiceDbContext db)
        => new(db, Options.Create(new MaerskCircuitOptions { Enabled = true }));

    private static MaerskExecutionResumeService Resume(ServiceDbContext db, bool enabled = true)
        => new(db, Options.Create(new MaerskCircuitOptions { Enabled = enabled }));

    private static async Task<(Guid ProviderId, Guid ExecutionId, BrowserProfile Profile)>
        CreateWaitingAsync(ServiceDbContext db, int maxAttempts = 3)
    {
        var provider = AgentProvider.Create("MAERSK", "Maersk",
            AgentProviderType.Maersk, "https://www.maersk.com",
            AgentExecutionStrategy.BrowserNetworkCapture, true, null);
        var definition = AgentDefinition.Create(provider.Id, "MAERSK_PHASE5_TEST",
            "Phase 5 test", null, AgentActionType.SearchOceanRates,
            AgentExecutionStrategy.BrowserNetworkCapture, null);
        var credential = AgentCredential.CreateEncrypted(
            provider.Id, "original-session", "encrypted-user", "encrypted-secret");
        var profile = BrowserProfile.Create(
            provider.Id, credential.Id, "original-browser",
            Guid.NewGuid().ToString("N"), "/unused/test-profile");
        var execution = AgentExecution.Create(
            definition.Id, provider.Id, null, credential.Id,
            AgentExecutionType.Scheduled, 1,
            """{"pol":"Shanghai","pod":"Caldera"}""",
            maxAttempts, Guid.NewGuid().ToString("N"));
        execution.Queue();
        execution.Start(DateTime.UtcNow);
        execution.WaitForAuthentication("maersk_hcaptcha_required", "Provider verification needed.");
        db.AgentProviders.Add(provider);
        db.AgentDefinitions.Add(definition);
        db.AgentCredentials.Add(credential);
        db.BrowserProfiles.Add(profile);
        db.AgentExecutions.Add(execution);
        await db.SaveChangesAsync();
        await Circuit(db).RecordFailureAsync(
            provider.Id, execution.Id, "maersk_hcaptcha_required");
        return (provider.Id, execution.Id, profile);
    }

    [TestMethod]
    public async Task ResumeRejectsAttestationWithoutFreshSessionAndOpenCircuit()
    {
        var conn = Connection();
        if (conn is null) return;
        await using var db = Db(conn);
        await db.Database.MigrateAsync();
        try
        {
            var seed = await CreateWaitingAsync(db);
            var actor = Guid.NewGuid();
            Assert.AreEqual(MaerskResumeOutcome.CircuitUnavailable,
                await Resume(db).ResumeAsync(seed.ExecutionId, actor,
                    "Provider verification declared but circuit still open", true));

            seed.Profile.Authenticate(DateTime.UtcNow);
            await db.SaveChangesAsync();
            Assert.IsTrue(await Circuit(db).ResetByOperatorAsync(
                seed.ProviderId, actor, "Legitimate verification completed in original browser", true));

            seed.Profile.SetStatus(BrowserProfileStatus.Blocked);
            await db.SaveChangesAsync();
            Assert.AreEqual(MaerskResumeOutcome.ProfileNotVerified,
                await Resume(db).ResumeAsync(seed.ExecutionId, actor,
                    "Browser has been blocked again", true));
            Assert.AreEqual(AgentExecutionStatus.WaitingForAuthentication,
                (await db.AgentExecutions.AsNoTracking()
                    .SingleAsync(x => x.Id == seed.ExecutionId)).Status);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [TestMethod]
    public async Task ResumeIsIdempotentAuditedAndPreservesOriginalIdentifiers()
    {
        var conn = Connection();
        if (conn is null) return;
        await using var db = Db(conn);
        await db.Database.MigrateAsync();
        try
        {
            var seed = await CreateWaitingAsync(db);
            var actor = Guid.NewGuid();
            var original = await db.AgentExecutions.AsNoTracking()
                .SingleAsync(x => x.Id == seed.ExecutionId);
            seed.Profile.Authenticate(DateTime.UtcNow);
            await db.SaveChangesAsync();
            Assert.IsTrue(await Circuit(db).ResetByOperatorAsync(
                seed.ProviderId, actor, "Operator completed normal provider verification", true));
            Assert.AreEqual(MaerskResumeOutcome.Resumed,
                await Resume(db).ResumeAsync(seed.ExecutionId, actor,
                    "Normal browser session verified and original execution approved", true));
            Assert.AreEqual(MaerskResumeOutcome.AlreadyQueued,
                await Resume(db).ResumeAsync(seed.ExecutionId, actor,
                    "Second click must be idempotent and not create new work", true));
            var result = await db.AgentExecutions.AsNoTracking()
                .SingleAsync(x => x.Id == seed.ExecutionId);
            Assert.AreEqual(AgentExecutionStatus.Queued, result.Status);
            Assert.AreEqual(original.Id, result.Id);
            Assert.AreEqual(original.InputJson, result.InputJson);
            Assert.AreEqual(original.CorrelationId, result.CorrelationId);
            Assert.AreEqual(original.Attempt, result.Attempt);
            Assert.AreEqual(1, await db.MaerskCircuitEvents.CountAsync(x =>
                x.ProviderId == seed.ProviderId
                && x.ExecutionId == seed.ExecutionId
                && x.EventType == "OperatorResume"));
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [TestMethod]
    public async Task ExhaustedAttemptsRemainWaitingAfterVerifiedRecovery()
    {
        var conn = Connection();
        if (conn is null) return;
        await using var db = Db(conn);
        await db.Database.MigrateAsync();
        try
        {
            var seed = await CreateWaitingAsync(db, maxAttempts: 1);
            var actor = Guid.NewGuid();
            seed.Profile.Authenticate(DateTime.UtcNow);
            await db.SaveChangesAsync();
            Assert.IsTrue(await Circuit(db).ResetByOperatorAsync(
                seed.ProviderId, actor, "Verified original browser session after challenge", true));
            Assert.AreEqual(MaerskResumeOutcome.AttemptsExhausted,
                await Resume(db).ResumeAsync(seed.ExecutionId, actor,
                    "Manual operator review requested after verification", true));
            Assert.AreEqual(AgentExecutionStatus.WaitingForAuthentication,
                (await db.AgentExecutions.AsNoTracking()
                    .SingleAsync(x => x.Id == seed.ExecutionId)).Status);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
}
