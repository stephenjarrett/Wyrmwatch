using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Wyrmwatch.Core;
using Wyrmwatch.Desktop;

namespace Wyrmwatch.Desktop.Tests;

// Actual headless wizard controls, synthetic saves and callback gates only.
// No game installation, Steam download, server process or user data is used.
public class CreateWizardUxTests
{
    [AvaloniaFact]
    public async Task KeyboardTabAndEnterAdvanceOnlyBeforeReviewAndEscapeClosesWhenIdle()
    {
        using var fixture = new Fixture(); var calls = 0;
        var dialog = new CreateServerDialog([], (_, _) => { calls++; return Task.CompletedTask; }, fixture.Root); dialog.Show();
        try
        {
            var name = Field<TextBox>(dialog, "CreateServerName"); name.Focus();
            dialog.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.True(Field<TextBox>(dialog, "CreateWorldName").IsFocused);
            dialog.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift); dialog.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
            Assert.True(name.IsFocused);
            var owner = Field<TextBox>(dialog, "CreateOwnerId"); owner.Text = "keyboard-owner"; owner.Focus();
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Until(() => Field<TextBox>(dialog, "CreateFolderName").IsEffectivelyVisible);
            var folder = Field<TextBox>(dialog, "CreateFolderName"); folder.Focus();
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); dialog.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Until(() => Field<StackPanel>(dialog, "CreateStructuredReview").IsEffectivelyVisible);
            Assert.False(Field<Button>(dialog, "CreateNext").IsDefault); Assert.Equal(0, calls);
            // Escape closes and disposes the native window immediately; no release can be sent afterward.
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(dialog.IsVisible); Assert.Equal(0, calls); Assert.False(Directory.Exists(fixture.Root));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ValidationPreservesValuesLabelsAndFocusesFirstInvalidField()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var dialog = new CreateServerDialog([], (_, _) => { calls++; return Task.CompletedTask; }, fixture.Root);
        dialog.Show();
        try
        {
            var name = Field<TextBox>(dialog, "CreateServerName");
            var owner = Field<TextBox>(dialog, "CreateOwnerId");
            name.Text = "";
            await dialog.AdvanceAsync();
            Assert.True(name.IsFocused);
            Assert.True(Field<TextBlock>(dialog, "CreateServerNameError").IsVisible);
            Assert.Equal("", owner.Text ?? "");
            name.Text = "Retained server";
            await dialog.AdvanceAsync();
            Assert.True(owner.IsFocused);
            Assert.Contains("Player ID", Field<TextBlock>(dialog, "CreateOwnerIdError").Text);
            Assert.Equal("Retained server", name.Text);
            Assert.Equal(owner, Assert.IsType<Label>(AutomationProperties.GetLabeledBy(owner)).Target);
            Assert.Contains("Player ID", AutomationProperties.GetName(owner));
            owner.Text = "fixture-owner";
            await dialog.AdvanceAsync();
            Field<TextBox>(dialog, "CreateFolderName").Text = "../invalid";
            await dialog.AdvanceAsync();
            Assert.True(Field<TextBox>(dialog, "CreateFolderName").IsFocused);
            Assert.Equal("../invalid", Field<TextBox>(dialog, "CreateFolderName").Text);
            Assert.True(Field<TextBlock>(dialog, "CreateFolderNameError").IsVisible);
            Assert.Equal("fixture-owner", owner.Text);
            Assert.Equal(0, calls); Assert.False(Directory.Exists(fixture.Root));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ImportReviewUsesCardsKeepsPasswordsHiddenAndOffersBackToServer()
    {
        using var fixture = new Fixture(); var source = fixture.World(); var original = File.ReadAllBytes(source);
        ServerCreationPlan? submitted = null;
        var dialog = new CreateServerDialog([], (_, _) => throw new InvalidOperationException("Create is not import"), fixture.Root,
            (plan, _, _) => { submitted = plan; return Task.CompletedTask; });
        dialog.Show();
        try
        {
            Assert.False(Field<Expander>(dialog, "ImportAdvancedWorld").IsExpanded);
            Assert.Equal("My World", Field<TextBox>(dialog, "CreateWorldName").Text);
            FillImport(dialog, source);
            Field<TextBox>(dialog, "CreateAdminPassword").Text = "private-admin-fixture";
            Field<TextBox>(dialog, "CreateWorldPassword").Text = "private-world-fixture";
            await dialog.AdvanceAsync(); await dialog.AdvanceAsync();
            Assert.True(Field<StackPanel>(dialog, "CreateStructuredReview").IsVisible);
            Assert.False(Field<TextBlock>(dialog, "CreateReview").IsVisible);
            Assert.Equal(Path.GetFileName(source), Field<TextBlock>(dialog, "ReviewSourceName").Text);
            Assert.Contains(original.Length.ToString("N0"), Field<TextBlock>(dialog, "ReviewSourceSize").Text);
            Assert.Contains("Structural checks do not prove game compatibility", Field<TextBlock>(dialog, "ReviewImportNote").Text);
            Assert.Contains("embedded world name", Field<TextBlock>(dialog, "ReviewImportNote").Text);
            var reviewText = string.Join("\n", Field<StackPanel>(dialog, "CreateStructuredReview").GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.DoesNotContain("private-admin-fixture", reviewText); Assert.DoesNotContain("private-world-fixture", reviewText);
            Assert.Null(submitted); Assert.Equal(original, File.ReadAllBytes(source));
            Capture(dialog, "import-structured-review.png");
            // Enter in a review card never submits the installation as a default action.
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            dialog.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Null(submitted);
            await dialog.AdvanceAsync();
            Assert.NotNull(submitted); Assert.Equal(submitted.Profile.Id, dialog.CompletedProfile?.Id);
            Assert.False(submitted.Profile.AutoBackup); Assert.False(submitted.Profile.AutoUpdate);
            Assert.Contains("Verify your imported world", Field<TextBlock>(dialog, "CreateReview").Text);
            Assert.Contains("Create a backup", Field<TextBlock>(dialog, "CreateReview").Text);
            Assert.True(Field<Button>(dialog, "CreateBackToServer").IsFocused);
            Capture(dialog, "import-completion.png");
            Field<Button>(dialog, "CreateBackToServer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(dialog.ReturnToServerRequested); Assert.False(dialog.IsVisible);
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WyrmwatchServers")));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task SetupShowsIndeterminatePhaseElapsedAndActivityAndBlocksEscapeUntilFinished()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource<Action<string>>(); var release = new TaskCompletionSource();
        var dialog = new CreateServerDialog([], async (_, report) => { entered.SetResult(report); await release.Task; }, fixture.Root);
        dialog.Show();
        try
        {
            Field<TextBox>(dialog, "CreateOwnerId").Text = "fixture-owner";
            await dialog.AdvanceAsync(); await dialog.AdvanceAsync();
            Capture(dialog, "create-structured-review.png");
            var pending = dialog.AdvanceAsync(); var report = await entered.Task;
            await Task.Run(() => report("Fixture: configuration verified")); Dispatcher.UIThread.RunJobs();
            Assert.True(Field<Border>(dialog, "CreateOperationCard").IsVisible);
            Assert.True(Field<ProgressBar>(dialog, "CreateProgress").IsVisible);
            Assert.True(Field<ProgressBar>(dialog, "CreateProgress").IsIndeterminate);
            Assert.Contains("Downloading and configuring", Field<TextBlock>(dialog, "CreateOperationPhase").Text);
            Assert.Contains("Elapsed", Field<TextBlock>(dialog, "CreateOperationTiming").Text);
            Assert.Contains("configuration verified", Field<TextBlock>(dialog, "CreateLatestActivity").Text);
            Assert.False(Field<Button>(dialog, "CreateCancel").IsEnabled);
            Capture(dialog, "create-operation.png");
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            dialog.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            dialog.Close(); Assert.True(dialog.IsVisible);
            release.SetResult(); await pending;
            Assert.False(Field<Border>(dialog, "CreateOperationCard").IsVisible);
            Assert.NotNull(dialog.CompletedProfile);
            Assert.False(Directory.Exists(fixture.Root));
        }
        finally { release.TrySetResult(); dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task PreparedSelectionShowsBusyVerificationBeforeReviewCompletes()
    {
        using var fixture = new Fixture();
        var profile = new ServerProfile { Name = "Prepared fixture", InstallPath = Path.Combine(fixture.Root, "WyrmwatchServers", "prepared"), BackupPath = Path.Combine(fixture.Root, "WyrmwatchBackups", "prepared") };
        var verify = new TaskCompletionSource<RecoveredSetup>();
        var actions = new PreparedSetupActions(_ => Task.FromResult<IReadOnlyList<PreparedSetupCandidate>>([new(profile.InstallPath, profile.Name, "prepared", "create", profile.Id)]),
            (_, _) => verify.Task, (_, _) => throw new InvalidOperationException("Review cannot resume"));
        var dialog = new CreateServerDialog([], (_, _) => throw new InvalidOperationException("Review cannot create"), fixture.Root, preparedActions: actions);
        dialog.Show();
        try
        {
            Field<ComboBox>(dialog, "PreparedSetupPicker").SelectedIndex = 0;
            Assert.True(Field<ProgressBar>(dialog, "CreateProgress").IsVisible);
            Assert.Contains("Verifying receipt", Field<TextBlock>(dialog, "CreateOperationPhase").Text);
            Assert.False(Field<Button>(dialog, "CreateNext").IsEnabled);
            Assert.False(Field<TextBox>(dialog, "CreateOwnerId").IsEffectivelyEnabled);
            Capture(dialog, "prepared-verification.png");
            verify.SetResult(new(profile, new() { ["DefaultWorldName"] = "Fixture", ["OwnerId"] = "fixture-owner", ["AdminPassword"] = "fixture-admin" }, null, "create", "fixture-token"));
            await Until(() => Field<Button>(dialog, "CreateNext").IsEnabled);
            Assert.True(Field<StackPanel>(dialog, "PreparedSetupReview").IsVisible);
            Assert.False(Field<ProgressBar>(dialog, "CreateProgress").IsVisible);
            Assert.False(Directory.Exists(fixture.Root));
        }
        finally { verify.TrySetCanceled(); dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ChangedImportSourceFocusesSourceAndRequiresStoppedConfirmationAgain()
    {
        using var fixture = new Fixture(); var source = fixture.World(); var calls = 0;
        var dialog = new CreateServerDialog([], (_, _) => throw new InvalidOperationException(), fixture.Root,
            (_, _, _) => { calls++; return Task.CompletedTask; }); dialog.Show();
        try
        {
            FillImport(dialog, source); await dialog.AdvanceAsync(); await dialog.AdvanceAsync();
            var bytes = File.ReadAllBytes(source); bytes[^1] = 2; File.WriteAllBytes(source, bytes);
            await dialog.AdvanceAsync();
            Assert.Equal(0, calls); Assert.True(Field<TextBox>(dialog, "ImportWorldSource").IsFocused);
            Assert.Equal(source, Field<TextBox>(dialog, "ImportWorldSource").Text);
            Assert.True(Field<TextBlock>(dialog, "ImportWorldSourceError").IsVisible);
            Assert.Contains("changed after review", Field<TextBlock>(dialog, "ImportWorldSourceError").Text);
            Assert.False(Field<CheckBox>(dialog, "ImportSourceStopped").IsChecked);
            Assert.Equal(bytes, File.ReadAllBytes(source));
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "WyrmwatchServers")));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task SetupFailureHasPersistentRecoveryGuidanceAndTechnicalDetails()
    {
        using var fixture = new Fixture();
        var dialog = new CreateServerDialog([], (_, _) => throw new IOException("Synthetic download failure"), fixture.Root); dialog.Show();
        try
        {
            Field<TextBox>(dialog, "CreateOwnerId").Text = "retained-owner";
            await dialog.AdvanceAsync(); await dialog.AdvanceAsync(); await dialog.AdvanceAsync();
            Assert.True(Field<Border>(dialog, "CreateFailureCard").IsVisible);
            Assert.Contains("Existing files were preserved", Field<TextBlock>(dialog, "CreateFailureHint").Text);
            Assert.Contains("Synthetic download failure", Field<TextBlock>(dialog, "CreateFailureDetails").Text);
            Field<Button>(dialog, "CreateFailureAction").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("retained-owner", Field<TextBox>(dialog, "CreateOwnerId").Text);
            Assert.True(Field<TextBox>(dialog, "CreateFolderName").IsEffectivelyVisible);
            Assert.False(Directory.Exists(fixture.Root)); Assert.Null(dialog.CompletedProfile);
        }
        finally { dialog.Close(); }
    }

    private static void FillImport(Window dialog, string source)
    {
        Field<TextBox>(dialog, "CreateOwnerId").Text = "fixture-owner";
        Field<TextBox>(dialog, "ImportWorldSource").Text = source;
        Field<CheckBox>(dialog, "ImportSourceStopped").IsChecked = true;
    }
    private static T Field<T>(Window dialog, string name) where T : Control => dialog.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    private static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 100 && !predicate(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(predicate());
    }
    private static void Capture(Window dialog, string name)
    {
        var directory = Environment.GetEnvironmentVariable("WYRM_TEST_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var original = Application.Current!.RequestedThemeVariant;
        try
        {
            foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Dark, Avalonia.Styling.ThemeVariant.Light })
            {
                Application.Current.RequestedThemeVariant = theme;
                dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = dialog.CaptureRenderedFrame(); Assert.NotNull(frame);
                var themedName = Path.GetFileNameWithoutExtension(name) + (theme == Avalonia.Styling.ThemeVariant.Dark ? "-dark.png" : "-light.png");
                frame.Save(Path.Combine(directory, themedName), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { Application.Current.RequestedThemeVariant = original; }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "wyrmwatch-wizard-ux-" + Guid.NewGuid().ToString("N"));
        public string World()
        {
            Directory.CreateDirectory(Root); var path = Path.Combine(Root, "Synthetic world.sav");
            File.WriteAllBytes(path, Chunk("SAVE", Chunk("INFO", [1, 0, 0, 0]).Concat(Chunk("GLOB", new byte[12])).ToArray()));
            return path;
        }
        private static byte[] Chunk(string kind, byte[] payload)
        {
            using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.ASCII, true);
            writer.Write(Encoding.ASCII.GetBytes(kind)); writer.Write((uint)payload.Length); writer.Write(payload); return output.ToArray();
        }
        public void Dispose()
        {
            if (!SafePaths.Within(Root, Path.GetTempPath()) || !Path.GetFileName(Root).StartsWith("wyrmwatch-wizard-ux-", StringComparison.Ordinal)) throw new IOException("Unsafe test cleanup path.");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
