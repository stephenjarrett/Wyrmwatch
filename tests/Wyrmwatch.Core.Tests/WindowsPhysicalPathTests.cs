using Wyrmwatch.Platform;

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
        }
        finally { Directory.Delete(directory); }
    }
}
