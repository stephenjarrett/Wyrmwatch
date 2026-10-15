using Wyrmwatch.Platform;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class WindowsPhysicalPathTests
{
    [Fact]
    public void ShortcutArgumentsRoundTripSpacesQuotesAndTrailingBackslashes()
    {
        if (!OperatingSystem.IsWindows()) return;
        string[] arguments = ["--minimized", "--data-dir", @"C:\A workspace\", "", "a\"quoted value", @"back\\slash"];
        Assert.Equal(arguments, WindowsCommandLine.ParseArguments(WindowsCommandLine.JoinArguments(arguments)));
    }
    [Fact]
    public void ExistingWorkspaceResolvesToAnExistingAbsolutePhysicalDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wyrmwatch-physical-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var physical = WindowsPhysicalPath.ExistingDirectory(directory);
            Assert.True(Path.IsPathFullyQualified(physical));
            Assert.True(Directory.Exists(physical));
            Assert.DoesNotContain(@"\\?\", physical);
            var profile = new ServerProfile { Id = "physical-workspace-fixture" };
            new AdminPasswordStore(new(directory)).Remember(profile, "physical-fixture-only");
            Assert.Equal("physical-fixture-only", new AdminPasswordStore(new(physical)).Read(profile));
            Assert.Equal(File.ReadAllBytes(Path.Combine(directory, "server-admin-passwords.json")),
                File.ReadAllBytes(Path.Combine(physical, "server-admin-passwords.json")));
        }
        finally { Directory.Delete(directory, true); }
    }
}
