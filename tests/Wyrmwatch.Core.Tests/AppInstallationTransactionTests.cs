using System.Security.Cryptography;
using System.Text.Json;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public sealed class AppInstallationTransactionTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "wyrmwatch-recovery-test-" + Guid.NewGuid().ToString("N"));
        internal readonly string Journal = Path.Combine(Path.GetTempPath(), "wyrmwatch-install-" + Guid.NewGuid().ToString("N"));
        internal string Installed => Path.Combine(Root, "app");
        internal string Incoming => Path.Combine(Root, "incoming");
        internal Fixture()
        {
            foreach (var folder in new[] { Installed, Incoming }) Directory.CreateDirectory(Path.Combine(folder, "agent"));
            foreach (var name in new[] { "Wyrmwatch.exe", "Wyrmwatch.dll", "Wyrmwatch.runtimeconfig.json", "agent/Wyrmwatch.Agent.exe" })
            { File.WriteAllText(Path.Combine(Installed, name), "working " + name); File.WriteAllText(Path.Combine(Incoming, name), "updated " + name); }
            File.WriteAllText(Path.Combine(Incoming, "added.dll"), "new package file");
            File.WriteAllText(Path.Combine(Installed, "unrelated.txt"), "user notes");
            File.WriteAllText(Path.Combine(Root, "world.sav"), "world bytes");
            File.WriteAllText(Path.Combine(Incoming, "retired-package-files.json"), "[\"obsolete.dll\",\"agent/createdump.exe\",\"agent/web.config\"]");
            File.WriteAllText(Path.Combine(Installed, "agent", "createdump.exe"), "previous runtime diagnostic helper");
            File.WriteAllText(Path.Combine(Installed, "agent", "web.config"), "previous generated host configuration");
            File.WriteAllText(Path.Combine(Installed, "obsolete.dll"), "previous dependency");
            var files = Directory.EnumerateFiles(Incoming, "*", SearchOption.AllDirectories).ToDictionary(
                path => Path.GetRelativePath(Incoming, path).Replace('\\', '/'), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            File.WriteAllBytes(Path.Combine(Incoming, AppInstallationTransaction.ManifestName), JsonSerializer.SerializeToUtf8Bytes(files));
        }
        internal void Prepare() => AppInstallationTransaction.Prepare(Installed, Incoming, Journal, "0.2.8");
        public void Dispose() { if (Directory.Exists(Journal)) Directory.Delete(Journal, true); Directory.Delete(Root, true); }
    }

    [Fact]
    public void PartialReplacementRestoresEveryPreviousFileAndPreservesUserData()
    {
        using var fixture = new Fixture(); fixture.Prepare();
        File.Copy(Path.Combine(fixture.Incoming, "Wyrmwatch.exe"), Path.Combine(fixture.Installed, "Wyrmwatch.exe"), true);
        File.Copy(Path.Combine(fixture.Incoming, "added.dll"), Path.Combine(fixture.Installed, "added.dll"));
        File.Delete(Path.Combine(fixture.Installed, "obsolete.dll"));
        Assert.Throws<IOException>(() => AppInstallationTransaction.VerifyInstalled(fixture.Journal));
        AppInstallationTransaction.Restore(fixture.Journal);
        Assert.Equal("working Wyrmwatch.exe", File.ReadAllText(Path.Combine(fixture.Installed, "Wyrmwatch.exe")));
        Assert.Equal("previous dependency", File.ReadAllText(Path.Combine(fixture.Installed, "obsolete.dll")));
        Assert.False(File.Exists(Path.Combine(fixture.Installed, "added.dll")));
        Assert.Equal("user notes", File.ReadAllText(Path.Combine(fixture.Installed, "unrelated.txt")));
        Assert.Equal("world bytes", File.ReadAllText(Path.Combine(fixture.Root, "world.sav")));
        Assert.Equal("previous runtime diagnostic helper", File.ReadAllText(Path.Combine(fixture.Journal, "original", "agent", "createdump.exe")));
        Assert.Equal("previous generated host configuration", File.ReadAllText(Path.Combine(fixture.Journal, "original", "agent", "web.config")));
        AppInstallationTransaction.Restore(fixture.Journal); // recovery is retryable
    }

    [Fact]
    public void CompletedReplacementVerifiesAndCopiesIndependentRecoveryHelper()
    {
        using var fixture = new Fixture(); fixture.Prepare();
        AppInstallationTransaction.CopySupport(fixture.Incoming, fixture.Journal);
        foreach (var path in Directory.EnumerateFiles(fixture.Incoming, "*", SearchOption.AllDirectories))
            File.Copy(path, Path.Combine(fixture.Installed, Path.GetRelativePath(fixture.Incoming, path)), true);
        AppInstallationTransaction.VerifyInstalled(fixture.Journal);
        Assert.Equal("updated Wyrmwatch.exe", File.ReadAllText(Path.Combine(fixture.Journal, "support", "Wyrmwatch.exe")));
        Assert.Equal("working Wyrmwatch.exe", File.ReadAllText(Path.Combine(fixture.Journal, "original", "Wyrmwatch.exe")));
    }

    [Fact]
    public void CorruptOriginalPreventsAnyRecoveryMutation()
    {
        using var fixture = new Fixture(); fixture.Prepare();
        File.WriteAllText(Path.Combine(fixture.Journal, "original", "Wyrmwatch.dll"), "corrupt");
        File.WriteAllText(Path.Combine(fixture.Installed, "Wyrmwatch.exe"), "partially updated");
        Assert.Throws<IOException>(() => AppInstallationTransaction.Restore(fixture.Journal));
        Assert.Equal("partially updated", File.ReadAllText(Path.Combine(fixture.Installed, "Wyrmwatch.exe")));
        Assert.True(File.Exists(Path.Combine(fixture.Journal, AppInstallationTransaction.JournalName)));
    }

    [Fact]
    public void InvalidManifestAndUnsafeJournalFailBeforeBackupOrReplacement()
    {
        using var fixture = new Fixture();
        Assert.Throws<IOException>(() => AppInstallationTransaction.Prepare(fixture.Installed, fixture.Incoming, fixture.Root, "0.2.8"));
        var manifest = Path.Combine(fixture.Incoming, AppInstallationTransaction.ManifestName);
        var files = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(manifest))!;
        files.Add("../world.sav", new string('a', 64));
        File.WriteAllBytes(manifest, JsonSerializer.SerializeToUtf8Bytes(files));
        Assert.Throws<IOException>(() => fixture.Prepare());
        Assert.False(Directory.Exists(fixture.Journal));
        Assert.Equal("working Wyrmwatch.exe", File.ReadAllText(Path.Combine(fixture.Installed, "Wyrmwatch.exe")));
    }

    [Fact]
    public void LockedTargetRetainsOriginalsAndRecoveryCanBeRetried()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture(); fixture.Prepare();
        var target = Path.Combine(fixture.Installed, "Wyrmwatch.exe");
        File.WriteAllText(target, "partial update");
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.ThrowsAny<Exception>(() => AppInstallationTransaction.Restore(fixture.Journal));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("working Wyrmwatch.exe", File.ReadAllText(Path.Combine(fixture.Journal, "original", "Wyrmwatch.exe")));
        AppInstallationTransaction.Restore(fixture.Journal);
        Assert.Equal("working Wyrmwatch.exe", File.ReadAllText(target));
    }
}
