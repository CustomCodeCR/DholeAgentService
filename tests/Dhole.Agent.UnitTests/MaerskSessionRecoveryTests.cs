using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Infrastructure.Browser;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskSessionRecoveryTests
{
    [DataTestMethod]
    [DataRow(BrowserProfileStatus.Unknown)]
    [DataRow(BrowserProfileStatus.Ready)]
    [DataRow(BrowserProfileStatus.LoginRequired)]
    [DataRow(BrowserProfileStatus.Authenticated)]
    [DataRow(BrowserProfileStatus.Authenticating)]
    [DataRow(BrowserProfileStatus.Expired)]
    [DataRow(BrowserProfileStatus.Error)]
    public void NonBlockedProfiles_ReuseExistingPersistentSession(BrowserProfileStatus status)
    {
        var recovery = CreateRecovery();
        var decision = recovery.Evaluate(status, AgentExecutionType.Scheduled);

        Assert.AreEqual(MaerskSessionRecoveryAction.ReusePersistentSession, decision.Action);
        Assert.IsTrue(decision.CanContinue);
    }

    [DataTestMethod]
    [DataRow(AgentExecutionType.Manual)]
    [DataRow(AgentExecutionType.Scheduled)]
    [DataRow(AgentExecutionType.Api)]
    public void ProviderBlocked_NeverRepairsOrResets(AgentExecutionType executionType)
    {
        var recovery = CreateRecovery();
        var decision = recovery.Evaluate(BrowserProfileStatus.Blocked, executionType);

        Assert.AreEqual(MaerskSessionRecoveryAction.StopForProviderVerification, decision.Action);
        Assert.AreEqual("maersk_browser_profile_blocked", decision.ErrorCode);
        Assert.IsFalse(decision.CanContinue);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            recovery.RepairApprovedTechnicalProfile(
                "MAERSK", Guid.NewGuid(), BrowserProfileStatus.Blocked, executionType));
    }

    [DataTestMethod]
    [DataRow(AgentExecutionType.Scheduled)]
    [DataRow(AgentExecutionType.Api)]
    [DataRow(AgentExecutionType.Grpc)]
    public void PendingRepair_RequiresAnOperatorInitiatedRun(AgentExecutionType executionType)
    {
        var decision = CreateRecovery().Evaluate(BrowserProfileStatus.ResetRequested, executionType);

        Assert.AreEqual(MaerskSessionRecoveryAction.AwaitManualRepair, decision.Action);
        Assert.IsFalse(decision.CanContinue);
    }

    [TestMethod]
    public void ExplicitManualRepair_ArchivesAndPreservesOriginalSession()
    {
        var root = TempRoot();
        var options = Options.Create(new BrowserOptions { ProfilesPath = root });
        var manager = new BrowserProfileManager(options);
        var recovery = new MaerskSessionRecoveryOrchestrator(new ImmediateLock(), manager);
        var credentialId = Guid.NewGuid();

        try
        {
            var before = manager.GetStoragePath("MAERSK", credentialId);
            File.WriteAllText(Path.Combine(before, "PreviousSessionMarker.txt"), "old");

            var decision = recovery.Evaluate(
                BrowserProfileStatus.ResetRequested, AgentExecutionType.Manual);
            Assert.AreEqual(MaerskSessionRecoveryAction.ApprovedTechnicalRepair, decision.Action);

            recovery.RepairApprovedTechnicalProfile(
                "MAERSK", credentialId,
                BrowserProfileStatus.ResetRequested, AgentExecutionType.Manual);

            var after = manager.GetStoragePath("MAERSK", credentialId);
            Assert.AreEqual(before, after);
            Assert.IsFalse(File.Exists(Path.Combine(after, "PreviousSessionMarker.txt")));
            var archives = Directory.GetDirectories(
                Path.GetDirectoryName(after)!,
                Path.GetFileName(after) + ".stale-*");
            Assert.HasCount(1, archives);
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(archives[0], "PreviousSessionMarker.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void BackupLimit_ProtectsExistingAndArchivedProfiles()
    {
        var root = TempRoot();
        var manager = new BrowserProfileManager(Options.Create(new BrowserOptions
        {
            ProfilesPath = root, MaxRetainedProfileBackups = 1
        }));
        var credentialId = Guid.NewGuid();

        try
        {
            var path = manager.GetStoragePath("MAERSK", credentialId);
            File.WriteAllText(Path.Combine(path, "first"), "original");
            manager.ResetStoragePath("MAERSK", credentialId);

            File.WriteAllText(Path.Combine(path, "second"), "new state");

            Assert.ThrowsExactly<IOException>(() =>
                manager.ResetStoragePath("MAERSK", credentialId));

            Assert.AreEqual("new state", File.ReadAllText(Path.Combine(path, "second")));
            var backups = Directory.GetDirectories(
                Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".stale-*");
            Assert.HasCount(1, backups);
            Assert.AreEqual("original", File.ReadAllText(Path.Combine(backups[0], "first")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SameProfile_CannotBeOpenedByTwoIndependentManagers()
    {
        var root = TempRoot();
        var options = Options.Create(new BrowserOptions
        {
            ProfilesPath = root, ProfileLockWaitSeconds = 1
        });
        var first = new FileSystemBrowserProfileExclusiveLock(options);
        var second = new FileSystemBrowserProfileExclusiveLock(options);
        var credential = Guid.NewGuid();

        try
        {
            var held = await first.TryAcquireAsync("MAERSK", credential);
            Assert.IsNotNull(held);
            var busy = await second.TryAcquireAsync("MAERSK", credential);
            Assert.IsNull(busy, "Concurrent browser profile access must fail closed.");

            // A different profile must remain usable even while this one is held.
            var otherProfile = await second.TryAcquireAsync("MAERSK", Guid.NewGuid());
            Assert.IsNotNull(otherProfile);
            await otherProfile.DisposeAsync();

            await held.DisposeAsync();
            var available = await second.TryAcquireAsync("MAERSK", credential);
            Assert.IsNotNull(available);
            await available.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProfileLock_RespectsCancellation()
    {
        var root = TempRoot();
        var manager = new FileSystemBrowserProfileExclusiveLock(
            Options.Create(new BrowserOptions { ProfilesPath = root }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => manager.TryAcquireAsync("MAERSK", Guid.NewGuid(), cts.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MaerskSessionRecoveryOrchestrator CreateRecovery()
        => new(new ImmediateLock(), new BrowserProfileManager(
            Options.Create(new BrowserOptions { ProfilesPath = TempRoot() })));

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "dhole-maersk-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ImmediateLock : IBrowserProfileExclusiveLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(
            string providerCode, Guid credentialId, CancellationToken cancellationToken = default)
            => Task.FromResult<IAsyncDisposable?>(new NoopHandle());

        private sealed class NoopHandle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
