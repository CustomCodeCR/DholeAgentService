using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Dhole.Agent.Persistence.Messaging;

/// <summary>
/// F6: scoped invalidation only. No CMS drafts, client/submission data,
/// agent credentials, request bodies, browser snapshots or private URLs.
/// REST owner authorization is required for any downstream subscription.
/// </summary>
public static class AgentRealtimeInvalidation
{
    public const string ReleaseId = "rt-20261107-01";
    public const string Stream = "dhole.agent.realtime.v1";
    private static long _lastMicrosecond;

    public static bool IsEnabled(IConfiguration? config) =>
        config?.GetValue<bool>("Features:UniversalRealtimeEnabled") == true &&
        config.GetValue<bool>("Realtime:Outbox:Enabled") &&
        config.GetValue<bool>("Realtime:BatchApproved") &&
        string.Equals(config["Realtime:ReleaseId"], ReleaseId, StringComparison.Ordinal);

    public static string EventType(string kind) => kind switch
    {
        "execution" => "agent.execution.changed.v1",
        "definition" => "agent.definition.changed.v1",
        "schedule" => "agent.schedule.changed.v1",
        _ => throw new ArgumentException("Unapproved Agent realtime resource.", nameof(kind))
    };

    public static long NextVersionHint()
    {
        while (true)
        {
            var micros = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
            var before = Volatile.Read(ref _lastMicrosecond);
            var next = Math.Max(micros, before + 1);
            if (Interlocked.CompareExchange(ref _lastMicrosecond, next, before) == before)
                return next;
        }
    }

    public static string Serialize(Guid eventId, Guid resourceId, string kind,
        long version, DateTimeOffset utc, string correlationId)
    {
        if (eventId == Guid.Empty || resourceId == Guid.Empty || version <= 0 ||
            utc.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Invalid realtime Agent resource.");
        var eventType = EventType(kind);
        return JsonSerializer.Serialize(new
        {
            eventId,
            eventType,
            schemaVersion = 1,
            aggregateType = kind,
            aggregateId = resourceId.ToString("D"),
            aggregateVersion = version,
            occurredAtUtc = utc,
            producer = "DholeAgentService",
            correlationId,
            audienceRef = $"agent:{kind}:{resourceId:D}",
            payload = new { changedFields = new[] { "resource" } }
        });
    }
}
