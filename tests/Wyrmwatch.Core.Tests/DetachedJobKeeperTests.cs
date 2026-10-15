using Wyrmwatch.Platform;

namespace Wyrmwatch.Core.Tests;

public class DetachedJobKeeperTests
{
    [Fact]
    public void ImmutableCacheSurvivesSourceReplacementAndRejectsTampering()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-keeper-cache-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app"); var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(app); Directory.CreateDirectory(workspace);
        try
        {
            var source = Path.Combine(app, "Wyrmwatch.Signal.JobKeeper.exe");
            File.WriteAllText(source, "first harmless executable-content fixture; never execute");
            var first = DetachedJobKeeper.Prepare(Path.Combine(app, "Wyrmwatch.Signal.exe"), workspace);
            Assert.StartsWith(Path.Combine(workspace, "process-support") + Path.DirectorySeparatorChar, first);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(first));
            Assert.Equal(first, DetachedJobKeeper.Prepare(Path.Combine(app, "Wyrmwatch.Signal.exe"), workspace));
            File.WriteAllText(source, "second harmless executable-content fixture; never execute");
            var second = DetachedJobKeeper.Prepare(Path.Combine(app, "Wyrmwatch.Signal.exe"), workspace);
            Assert.NotEqual(first, second); Assert.True(File.Exists(first));
            File.WriteAllText(second + ".config", "unexpected configuration fixture");
            Assert.Throws<IOException>(() => DetachedJobKeeper.Prepare(Path.Combine(app, "Wyrmwatch.Signal.exe"), workspace));
            File.Delete(second + ".config");
            File.WriteAllText(second, "tampered cache fixture; never execute");
            Assert.Throws<IOException>(() => DetachedJobKeeper.Prepare(Path.Combine(app, "Wyrmwatch.Signal.exe"), workspace));
            Assert.Equal("tampered cache fixture; never execute", File.ReadAllText(second));
            Assert.Empty(Directory.GetFiles(workspace, "*.tmp", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingOrOversizedSourceDoesNotCreateExecutableCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-keeper-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var signal = Path.Combine(root, "Wyrmwatch.Signal.exe"); var workspace = Path.Combine(root, "workspace");
            Assert.Throws<IOException>(() => DetachedJobKeeper.Prepare(signal, workspace));
            File.WriteAllBytes(Path.Combine(root, "Wyrmwatch.Signal.JobKeeper.exe"), new byte[262145]);
            Assert.Throws<IOException>(() => DetachedJobKeeper.Prepare(signal, workspace));
            Assert.False(Directory.Exists(workspace));
            File.WriteAllText(Path.Combine(root, "Wyrmwatch.Signal.JobKeeper.exe"), "harmless fixture");
            var longWorkspace = Path.Combine(root, new string('x', 200));
            Assert.Throws<IOException>(() => DetachedJobKeeper.Prepare(signal, longWorkspace));
            Assert.False(Directory.Exists(longWorkspace));
        }
        finally { Directory.Delete(root, true); }
    }
}
