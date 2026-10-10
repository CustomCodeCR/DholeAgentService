using System.Text.Json;
using Dhole.Agent.Contracts.Agents;
using Dhole.Agent.Persistence.DbContexts;
using Dhole.Agent.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.IntegrationTests;

[TestClass]
public sealed class MaerskOperationsSnapshotTests
{
    [TestMethod]
    public async Task CircuitAuditEvents_AreMappedAndCanBeQueriedWithoutSecrets()
    {
        await using var db = new ServiceDbContext(
            new DbContextOptionsBuilder<ServiceDbContext>()
                .UseInMemoryDatabase($"maersk-operations-{Guid.NewGuid():N}")
                .Options);
        await db.Database.EnsureCreatedAsync();

        var providerId = Guid.NewGuid();
        var incident = new MaerskCircuitEventRecord
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            EventType = "Opened",
            ReasonCode = "maersk_hcaptcha_required",
            OperatorReason = "Internal operator reason is never returned to UI",
            OccurredAtUtc = DateTime.UtcNow
        };
        db.MaerskCircuitEvents.Add(incident);
        await db.SaveChangesAsync();

        var recent = await db.MaerskCircuitEvents.AsNoTracking()
            .Where(x => x.ProviderId == providerId)
            .OrderByDescending(x => x.OccurredAtUtc)
            .Select(x => new MaerskOperationEventDto(
                x.Id, x.EventType, x.ReasonCode, x.ActorId, x.OccurredAtUtc))
            .ToArrayAsync();

        Assert.AreEqual(1, recent.Length);
        Assert.AreEqual("maersk_hcaptcha_required", recent[0].ReasonCode);
        Assert.IsFalse(JsonSerializer.Serialize(recent).Contains(
            "Internal operator reason", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DisabledAndStoredCircuitState_AreExplicitlyDifferentFields()
    {
        var dto = new MaerskOperationCircuitDto(
            false, "Disabled", false, null, null, 0, null)
        { PersistedState = "Open", ConfigurationSource = "Default" };
        Assert.AreEqual("Disabled", dto.State);
        Assert.AreEqual("Open", dto.PersistedState);
        Assert.IsFalse(dto.FeatureEnabled);
    }

    [TestMethod]
    public void OverviewDto_ExcludesCredentialsStorageAndRawResponses()
    {
        var properties = typeof(MaerskOperationsDto).Assembly
            .GetTypes()
            .Where(t => t.Name.StartsWith("MaerskOperation", StringComparison.Ordinal))
            .SelectMany(t => t.GetProperties())
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var forbidden in new[]
        {
            "StoragePath", "Password", "Cookies", "UsernameSecretKey",
            "InputJson", "OutputJson", "ErrorMessage", "PromptSnapshot",
            "OperatorReason", "ResponseJson", "ProfileKey"
        })
            Assert.IsFalse(properties.Contains(forbidden),
                $"The operational overview must not expose {forbidden}.");
    }
}
