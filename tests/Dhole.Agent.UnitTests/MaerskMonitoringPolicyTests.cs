using Dhole.Agent.Application.Runtime;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskMonitoringPolicyTests
{
    [TestMethod]
    public void AllSignalsProduceStableNonSensitiveKeys()
    {
        var options = new MaerskMonitoringOptions { Enabled = true, FailureCountThreshold = 5 };
        var signals = new MaerskHealthSignals(true, true, 2, 10, 1, 5);
        var alerts = MaerskMonitoringPolicy.Evaluate(signals, options);
        Assert.AreEqual(6, alerts.Count);
        Assert.AreEqual(6, alerts.Select(a => a.Key).Distinct().Count());
        Assert.IsTrue(alerts.All(x => !string.IsNullOrWhiteSpace(x.Code)));
        Assert.AreEqual("Critical", alerts.Single(x =>
            x.Key == MaerskMonitoringPolicy.ProviderAccess).Severity);
        Assert.AreEqual("Critical", alerts.Single(x =>
            x.Key == MaerskMonitoringPolicy.BrowserBlocked).Severity);
    }

    [TestMethod]
    public void HealthyProviderHasNoAlert_AndFailureThresholdIsInclusive()
    {
        var options = new MaerskMonitoringOptions();
        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(0, MaerskMonitoringPolicy.Evaluate(
            new MaerskHealthSignals(false, false, 0, 0, 0, 0), options).Count);
        Assert.AreEqual(0, MaerskMonitoringPolicy.Evaluate(
            new MaerskHealthSignals(false, false, 0, 0, 0, 4), options).Count);
        var alerts = MaerskMonitoringPolicy.Evaluate(
            new MaerskHealthSignals(false, false, 0, 0, 0, 5), options);
        Assert.AreEqual(MaerskMonitoringPolicy.FailedExecutions, alerts.Single().Key);
    }

    [TestMethod]
    public void InvalidSettingsAreRejected()
    {
        var o = new MaerskMonitoringOptions { PollIntervalSeconds = 1 };
        Assert.ThrowsExactly<InvalidOperationException>(() => o.Validate());
        o.PollIntervalSeconds = 60;
        o.FailureCountThreshold = 1;
        Assert.ThrowsExactly<InvalidOperationException>(() => o.Validate());
    }
}
