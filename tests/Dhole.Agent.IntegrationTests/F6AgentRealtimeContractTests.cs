using System.Text.Json;
using Dhole.Agent.Persistence.Messaging;
using Microsoft.Extensions.Configuration;

namespace Dhole.Agent.IntegrationTests;

[TestClass]
public sealed class F6AgentRealtimeContractTests
{
    [TestMethod]
    public void Flags_wrong_release_and_no_approval_fail_closed()
    {
        Assert.IsFalse(AgentRealtimeInvalidation.IsEnabled(null));
        var flags = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> {
                ["Features:UniversalRealtimeEnabled"] = "true",
                ["Realtime:Outbox:Enabled"] = "true",
                ["Realtime:BatchApproved"] = "false",
                ["Realtime:ReleaseId"] = AgentRealtimeInvalidation.ReleaseId
            }).Build();
        Assert.IsFalse(AgentRealtimeInvalidation.IsEnabled(flags));
        flags["Realtime:BatchApproved"] = "true";
        Assert.IsTrue(AgentRealtimeInvalidation.IsEnabled(flags));
        flags["Realtime:ReleaseId"] = "wrong";
        Assert.IsFalse(AgentRealtimeInvalidation.IsEnabled(flags));
    }

    [TestMethod]
    public void Job_agent_definition_and_schedule_have_distinct_safe_metadata()
    {
        var id = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        foreach (var kind in new[] { "execution", "definition", "schedule" })
        {
            var json = AgentRealtimeInvalidation.Serialize(eventId, id, kind,
                AgentRealtimeInvalidation.NextVersionHint(),
                DateTimeOffset.UtcNow, "agent-f6-ci");
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            Assert.AreEqual("agent." + kind + ".changed.v1", root.GetProperty("eventType").GetString());
            Assert.AreEqual($"agent:{kind}:{id:D}", root.GetProperty("audienceRef").GetString());
            CollectionAssert.AreEquivalent(new[] { "changedFields" },
                root.GetProperty("payload").EnumerateObject().Select(x => x.Name).ToArray());
            foreach (var forbidden in new[] { "credential", "password", "inputJson", "output", "browser", "token" })
                Assert.IsFalse(json.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
        }
    }

    [TestMethod]
    public void Reject_unknown_resource_and_missing_id()
    {
        var id = Guid.NewGuid();
        Assert.ThrowsExactly<ArgumentException>(() => AgentRealtimeInvalidation.EventType("secret"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            AgentRealtimeInvalidation.Serialize(id, Guid.Empty, "execution", 1,
                DateTimeOffset.UtcNow, "x"));
    }
}
