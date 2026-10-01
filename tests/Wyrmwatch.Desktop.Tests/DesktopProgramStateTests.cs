using Avalonia.Headless.XUnit;
using Wyrmwatch.Core;
using Wyrmwatch.Desktop;

namespace Wyrmwatch.Desktop.Tests;

public class DesktopProgramStateTests : IsolatedDesktopTest
{
    [AvaloniaFact]
    public void DemoUiThenNormalPrerequisiteRemainsIsolatedInThatOrder()
    {
        using (var demo = new SmokeTests())
        {
            demo.PagesThemesAndFocusedFormSurviveStatusUpdates();
            Assert.True(Program.Demo);
            var model = new WorkspaceModel { SelectedProfile = new ServerProfile(), ServerRunning = false };
            Assert.False(model.CanStart);
            Assert.Contains("demo workspace", model.StartHint);
        }
        Assert.False(Program.Demo);
        using var normal = new WorkspaceModelTests();
        normal.AnotherObservedServerBlocksStartWithDestinationAndUnknownStateStaysBlocked();
    }

    [Fact]
    public void ScopeRestoresCallerFlagsAndDirectoryEvenAfterFailure()
    {
        var originalDirectory = Program.DataDirectory;
        Program.Demo = Program.Minimized = Program.ApplyStartup = true;
        Program.HeadlessTest = false;
        string? temporaryDirectory = null;
        Action fail = () =>
        {
            using var scope = new Probe();
            Assert.False(Program.Demo); Assert.True(Program.HeadlessTest);
            Assert.False(Program.Minimized); Assert.False(Program.ApplyStartup);
            Assert.NotEqual(originalDirectory, Program.DataDirectory);
            temporaryDirectory = Program.DataDirectory;
            Directory.CreateDirectory(temporaryDirectory);
            File.WriteAllText(Path.Combine(temporaryDirectory, "fixture.txt"), "disposable fixture");
            Program.Demo = true;
            throw new InvalidOperationException("Simulated assertion failure");
        };
        Assert.Throws<InvalidOperationException>(fail);
        Assert.True(Program.Demo); Assert.False(Program.HeadlessTest);
        Assert.True(Program.Minimized); Assert.True(Program.ApplyStartup);
        Assert.Equal(originalDirectory, Program.DataDirectory);
        Assert.NotNull(temporaryDirectory); Assert.False(Directory.Exists(temporaryDirectory));
        // The outer per-test scope also restores these deliberately changed flags.
    }

    private sealed class Probe : IsolatedDesktopTest { }
}
