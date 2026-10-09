using Dhole.Agent.Infrastructure.Browser;
using Microsoft.Extensions.Options;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class BrowserProfileRepairTests
{
    [TestMethod]
    public void RepairStoragePath_ArchivesPreviousChromiumDataBeforeCreatingFreshDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dhole-agent-repair-" + Guid.NewGuid().ToString("N"));
        var manager = new BrowserProfileManager(Options.Create(new BrowserOptions { ProfilesPath = directory }));
        var credentialId = Guid.NewGuid();

        try
        {
            var original = manager.GetStoragePath("MAERSK", credentialId);
            File.WriteAllText(Path.Combine(original, "PreviousSessionMarker.txt"), "preserve");

            var fresh = manager.ResetStoragePath("MAERSK", credentialId);

            Assert.AreEqual(original, fresh);
            Assert.IsTrue(Directory.Exists(fresh));
            Assert.IsFalse(File.Exists(Path.Combine(fresh, "PreviousSessionMarker.txt")));

            var archives = Directory.GetDirectories(Path.GetDirectoryName(fresh)!, Path.GetFileName(fresh) + ".stale-*");
            Assert.AreEqual(1, archives.Length);
            Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(archives[0], "PreviousSessionMarker.txt")));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
