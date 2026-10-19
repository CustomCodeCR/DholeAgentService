using Dhole.Agent.Infrastructure.Browser;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class ChromiumStaleSingletonRecoveryTests
{
    [TestMethod]
    public void StaleLinksAreArchived_WithoutTouchingSessionOrCookies()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-chromium-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sessionPath = Path.Combine(root, "Default", "Network");
            Directory.CreateDirectory(sessionPath);
            File.WriteAllText(Path.Combine(sessionPath, "Cookies"), "keep-session-bytes");
            File.WriteAllText(Path.Combine(root, "Local State"), "keep-local-state");

            var deadPid = 2000000000;
            File.CreateSymbolicLink(Path.Combine(root, "SingletonLock"),
                "prior-container-" + deadPid);
            File.CreateSymbolicLink(Path.Combine(root, "SingletonSocket"),
                "/tmp/missing-chromium-socket-" + Guid.NewGuid().ToString("N"));
            File.CreateSymbolicLink(Path.Combine(root, "SingletonCookie"),
                "/tmp/missing-chromium-cookie-" + Guid.NewGuid().ToString("N"));

            Assert.IsTrue(ChromiumStaleSingletonRecovery.TryArchiveOrphanedLinks(root, out var diagnostic), diagnostic);
            Assert.IsFalse(File.Exists(Path.Combine(root, "SingletonLock")));
            Assert.IsNull(new FileInfo(Path.Combine(root, "SingletonLock")).LinkTarget);
            var archive = Directory.GetDirectories(root, ".dhole-orphaned-singletons-*");
            Assert.AreEqual(1, archive.Length);
            foreach (var name in new[] { "SingletonLock", "SingletonSocket", "SingletonCookie" })
                Assert.IsNotNull(new FileInfo(Path.Combine(archive[0], name)).LinkTarget);
            Assert.AreEqual("keep-session-bytes", File.ReadAllText(Path.Combine(sessionPath, "Cookies")));
            Assert.AreEqual("keep-local-state", File.ReadAllText(Path.Combine(root, "Local State")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LiveSocketOrRegularLockRefusesRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-chromium-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "SingletonLock"), "not-a-symlink");
            File.CreateSymbolicLink(Path.Combine(root, "SingletonSocket"),
                "/tmp/missing-chromium-socket-" + Guid.NewGuid().ToString("N"));
            File.CreateSymbolicLink(Path.Combine(root, "SingletonCookie"),
                "/tmp/missing-chromium-cookie-" + Guid.NewGuid().ToString("N"));
            Assert.IsFalse(ChromiumStaleSingletonRecovery.TryArchiveOrphanedLinks(root));
            Assert.AreEqual("not-a-symlink", File.ReadAllText(Path.Combine(root, "SingletonLock")));

            File.Delete(Path.Combine(root, "SingletonLock"));
            File.CreateSymbolicLink(Path.Combine(root, "SingletonLock"), "prior-container-2000000000");
            var socket = Path.Combine(root, "live-socket-placeholder");
            File.WriteAllText(socket, "still-in-use");
            File.Delete(Path.Combine(root, "SingletonSocket"));
            File.CreateSymbolicLink(Path.Combine(root, "SingletonSocket"), socket);
            Assert.IsFalse(ChromiumStaleSingletonRecovery.TryArchiveOrphanedLinks(root));
            Assert.IsTrue(File.Exists(socket));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void EmptyAndUnownedProfilesNeverCreateNewDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-chromium-empty-" + Guid.NewGuid().ToString("N"));
        Assert.IsFalse(ChromiumStaleSingletonRecovery.TryArchiveOrphanedLinks(root));
        Assert.IsFalse(Directory.Exists(root));
    }
}
