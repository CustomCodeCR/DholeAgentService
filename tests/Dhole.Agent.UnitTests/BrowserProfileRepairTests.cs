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
    [TestMethod]
    public void DifferentGenerationsCannotReadOrReuseEachOthersProfileVolume()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-generation-" + Guid.NewGuid().ToString("N"));
        var credential = Guid.NewGuid();
        try
        {
            var prod2 = new BrowserProfileManager(Options.Create(
                new BrowserOptions { ProfilesPath = root, Generation = "prod2" }));
            var profile = prod2.GetStoragePath("MAERSK", credential);
            File.WriteAllText(Path.Combine(profile, "Local State"), "original-session");

            var prod3 = new BrowserProfileManager(Options.Create(
                new BrowserOptions { ProfilesPath = root, Generation = "prod3" }));
            Assert.ThrowsException<IOException>(() => prod3.GetStoragePath("MAERSK", credential));
            Assert.AreEqual("original-session", File.ReadAllText(Path.Combine(profile, "Local State")));
            Assert.AreEqual("prod2", File.ReadAllText(Path.Combine(root, ".dhole-generation")).Trim());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void SeparateGenerationVolumesCanUseSameCredentialIndependently()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-generations-" + Guid.NewGuid().ToString("N"));
        var credential = Guid.NewGuid();
        try
        {
            var a = new BrowserProfileManager(Options.Create(new BrowserOptions
                { ProfilesPath = Path.Combine(root, "prod1"), Generation = "prod1" }));
            var b = new BrowserProfileManager(Options.Create(new BrowserOptions
                { ProfilesPath = Path.Combine(root, "prod2"), Generation = "prod2" }));
            var aPath = a.GetStoragePath("MAERSK", credential);
            var bPath = b.GetStoragePath("MAERSK", credential);
            Assert.AreNotEqual(aPath, bPath);
            File.WriteAllText(Path.Combine(aPath, "Local State"), "first-generation");
            File.WriteAllText(Path.Combine(bPath, "Local State"), "second-generation");
            Assert.AreEqual("first-generation", File.ReadAllText(Path.Combine(aPath, "Local State")));
            Assert.AreEqual("second-generation", File.ReadAllText(Path.Combine(bPath, "Local State")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

}
