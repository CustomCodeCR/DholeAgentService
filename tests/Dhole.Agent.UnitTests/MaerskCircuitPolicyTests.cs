using Dhole.Agent.Application.Runtime;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskCircuitPolicyTests
{
    [TestMethod]
    public void Closed_IsAvailable()
    {
        var state = MaerskCircuitSnapshot.Closed(Guid.NewGuid());
        Assert.IsTrue(state.CanSchedule(DateTime.UtcNow));
        Assert.IsFalse(state.RequiresOperator);
    }

    [TestMethod]
    public void ProviderRestriction_DoesNotAutoExpire()
    {
        var state = new MaerskCircuitSnapshot(
            Guid.NewGuid(), "Open", true, "maersk_hcaptcha_required",
            DateTime.UtcNow.AddDays(-1), 1, null);

        Assert.IsFalse(state.CanSchedule(DateTime.UtcNow));
    }

    [TestMethod]
    public void TechnicalCooldown_AllowsOnlyTransitionToAtomicProbe()
    {
        var now = DateTime.UtcNow;
        var blocked = new MaerskCircuitSnapshot(Guid.NewGuid(), "Open", false,
            "maersk_offer_timeout", now.AddMinutes(15), 3, null);
        Assert.IsFalse(blocked.CanSchedule(now));

        var expired = blocked with { OpenUntilUtc = now.AddSeconds(-1) };
        Assert.IsTrue(expired.CanSchedule(now));

        var probing = expired with { State = "HalfOpen", ProbeExecutionId = Guid.NewGuid() };
        Assert.IsFalse(probing.CanSchedule(now));
    }

    [TestMethod]
    public void DisabledByDefaultAndBoundsValidated()
    {
        var options = new MaerskCircuitOptions();
        Assert.IsFalse(options.Enabled);
        options.Validate();
        Assert.AreEqual(3, options.TransientFailureThreshold);
        Assert.AreEqual(900, options.TechnicalCooldownSeconds);
        Assert.AreEqual(1800, options.ProviderCooldownSeconds);

        options.TransientFailureThreshold = 1;
        Assert.ThrowsExactly<InvalidOperationException>(() => options.Validate());
        options.TransientFailureThreshold = 3;
        options.TechnicalCooldownSeconds = 3;
        Assert.ThrowsExactly<InvalidOperationException>(() => options.Validate());
    }
}
