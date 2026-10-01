using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wyrmwatch.Core;
using Wyrmwatch.Desktop;

namespace Wyrmwatch.Desktop.Tests;

public class VerticalAlignmentTests : IsolatedDesktopTest
{
    [AvaloniaFact]
    public void SharedInputsCenterActualTextAtNormalAndExpandedHeightsInBothThemes()
    {
        var errors = new List<string>();
        var original = Application.Current!.RequestedThemeVariant;
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        var fields = new List<Control>();
        foreach (var height in new[] { 38d, 58d })
        {
            fields.Add(new TextBox { Name = "Text" + height, Text = "7777", Height = height, Foreground = Brushes.White, Background = Brushes.Black });
            fields.Add(new TextBox { Name = "ReadOnly" + height, Text = "Read only", IsReadOnly = true, Height = height });
            fields.Add(new TextBox { Name = "Password" + height, Text = "secret fixture", PasswordChar = '?', Height = height });
            fields.Add(new ComboBox { Name = "Choice" + height, ItemsSource = new[] { "Selected choice" }, SelectedIndex = 0, Height = height });
            fields.Add(new NumericUpDown { Name = "Number" + height, Value = 7777, FormatString = "0", Height = height });
            fields.Add(new Button { Name = "Action" + height, Content = "Continue", Height = height });
        }
        foreach (var field in fields) panel.Children.Add(field);
        var window = new Window { Width = 480, Height = 850, Content = panel };
        window.Show();
        try
        {
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current.RequestedThemeVariant = theme;
                Render(window);
                foreach (var field in fields) CheckCenter(window, field, errors, theme.ToString());
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                foreach (var field in fields.OfType<TextBox>().Where(t => t.Name!.StartsWith("Text")))
                    CheckInkCenter(window, frame, field, errors, theme.ToString());
                Capture(window, "alignment-controls-" + theme.ToString().ToLowerInvariant() + ".png");
            }
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        }
        finally { window.Close(); Application.Current.RequestedThemeVariant = original; }
    }

    [AvaloniaFact]
    public void WorkspaceCentersFieldsAndActionsWhileKeepingLogsTopAlignedAndPathsUnclipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-alignment-" + Guid.NewGuid().ToString("N"));
        Program.HeadlessTest = true; Program.Demo = false; Program.DataDirectory = root;
        var original = Application.Current!.RequestedThemeVariant;
        var window = new MainWindow(); window.Show();
        var errors = new List<string>();
        try
        {
            var model = (WorkspaceModel)window.DataContext!;
            var profile = new ServerProfile { Name = "Alignment fixture", InstallPath = Path.Combine(root, new string('a', 80), "server"), BackupPath = Path.Combine(root, "backups") };
            model.Profiles.Add(profile); model.SelectedProfile = profile; model.SyncRows(); model.ServerRunning = false;
            Field<TextBox>(window, "InstallFolder").Text = profile.InstallPath;
            Field<TextBox>(window, "DataFolder").Text = profile.SavedPath;
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current.RequestedThemeVariant = theme;
                var suffix = theme.ToString().ToLowerInvariant();
                foreach (var page in new[] { WorkspacePage.Servers, WorkspacePage.ServerSettings, WorkspacePage.Automation, WorkspacePage.AppSettings, WorkspacePage.Activity, WorkspacePage.Backups })
                {
                    if (page == WorkspacePage.Backups) { model.RecoveryPending = true; model.Status = "Recovery required"; }
                    window.ShowPage(page); Render(window);
                    foreach (var control in window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Height > 0))
                    {
                        if (control is TextBox text)
                        {
                            if (text.AcceptsReturn)
                            {
                                Assert.Equal(VerticalAlignment.Top, text.VerticalContentAlignment);
                                var presenter = text.GetVisualDescendants().OfType<TextPresenter>().Single();
                                Assert.True(presenter.TranslatePoint(default, text)!.Value.Y <= text.Padding.Top + text.BorderThickness.Top + 1);
                            }
                            else CheckCenter(window, text, errors, suffix + " " + page);
                        }
                        else if (control.GetType() == typeof(Button) && control is Button button && button.GetVisualAncestors().All(a => a is not NumericUpDown && a is not Expander && a is not ComboBox))
                            CheckCenter(window, button, errors, suffix + " " + page);
                        else if (control is ComboBox or NumericUpDown || control is Border badge && badge.Classes.Contains("status-chip"))
                            CheckCenter(window, control, errors, suffix + " " + page);
                    }
                    if (page == WorkspacePage.ServerSettings)
                    {
                        var path = Field<TextBox>(window, "InstallFolder");
                        var presenter = path.GetVisualDescendants().OfType<TextPresenter>().Single();
                        Assert.True(presenter.TextLayout.TextLines.Count > 1, "Long path fixture must wrap.");
                        Assert.True(presenter.TextLayout.Height <= path.Bounds.Height - path.Padding.Top - path.Padding.Bottom - path.BorderThickness.Top - path.BorderThickness.Bottom + 1, "Wrapped path is clipped.");
                        var name = Field<TextBox>(window, "ProfileName"); name.Text = "Unsaved focused fixture"; name.Focus();
                        model.Cpu = "12%"; model.Status = "Stopped"; Render(window);
                        Assert.True(name.IsFocused); Assert.Equal("Unsaved focused fixture", name.Text);
                    }
                    Capture(window, "alignment-" + page.ToString().ToLowerInvariant() + "-" + suffix + ".png");
                    model.RecoveryPending = false;
                }
            }
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        }
        finally { window.Close(); Application.Current.RequestedThemeVariant = original; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task WizardCentersTextAndPortWithoutClippingManagedLocationsInBothThemes()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-alignment-" + Guid.NewGuid().ToString("N"));
        var original = Application.Current!.RequestedThemeVariant;
        var errors = new List<string>();
        var dialog = new CreateServerDialog([], (_, _) => throw new InvalidOperationException("Alignment review cannot create a server."), root); dialog.Show();
        try
        {
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current.RequestedThemeVariant = theme; Render(dialog);
                CheckCenter(dialog, Field<TextBox>(dialog, "CreateServerName"), errors, theme.ToString());
                CheckCenter(dialog, Field<Button>(dialog, "CreateNext"), errors, theme.ToString());
                Capture(dialog, "alignment-wizard-world-" + theme.ToString().ToLowerInvariant() + ".png");
            }
            Field<TextBox>(dialog, "CreateOwnerId").Text = "fixture-owner"; await dialog.AdvanceAsync();
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current.RequestedThemeVariant = theme; Render(dialog);
                foreach (var name in new[] { "CreateAdminPassword", "CreateFolderName", "CreateManagedLocations" }) CheckCenter(dialog, Field<TextBox>(dialog, name), errors, theme.ToString());
                CheckCenter(dialog, Field<NumericUpDown>(dialog, "CreatePort"), errors, theme.ToString());
                var locations = Field<TextBox>(dialog, "CreateManagedLocations");
                var presenter = locations.GetVisualDescendants().OfType<TextPresenter>().Single();
                Assert.True(presenter.TextLayout.TextLines.Count >= 2);
                Assert.True(presenter.TextLayout.Height <= locations.Bounds.Height - locations.Padding.Top - locations.Padding.Bottom - 2 + 1);
                Capture(dialog, "alignment-wizard-storage-" + theme.ToString().ToLowerInvariant() + ".png");
            }
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors)); Assert.False(Directory.Exists(root));
        }
        finally { dialog.Close(); Application.Current.RequestedThemeVariant = original; }
    }

    private static void CheckCenter(Window window, Control control, List<string> errors, string context)
    {
        Control? text; double height;
        var presenter = control.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (presenter is not null) { text = presenter; height = presenter.TextLayout.Height; }
        else { var block = control.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)); text = block; height = block?.TextLayout.Height ?? 0; }
        if (text is null || height <= 0) return;
        var top = text.TranslatePoint(default, control)!.Value.Y;
        var delta = top + height / 2 - control.Bounds.Height / 2;
        var line = $"{context} {control.GetType().Name} {control.Name}: height={control.Bounds.Height:F2}, textTop={top:F2}, textHeight={height:F2}, centerDelta={delta:F2}px";
        Log(line);
        // Fluent dropdown glyphs are optically centered in native captures even though
        // Inter's full line box sits about 1.5 px above the control's geometric center.
        var tolerance = control is ComboBox ? 2 : 1;
        if (Math.Abs(delta) > tolerance) errors.Add(line);
    }

    private static void CheckInkCenter(Window window, Bitmap frame, TextBox field, List<string> errors, string context)
    {
        using var copy = new WriteableBitmap(frame.PixelSize, frame.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = copy.Lock();
        frame.CopyPixels(buffer);
        var stride = buffer.RowBytes; var length = stride * frame.PixelSize.Height;
        {
            var bytes = new byte[length]; Marshal.Copy(buffer.Address, bytes, 0, length);
            var origin = field.TranslatePoint(default, window)!.Value;
            var rows = new List<int>();
            for (var y = (int)origin.Y + 5; y < (int)(origin.Y + field.Bounds.Height) - 5; y++)
            for (var x = (int)origin.X + 7; x < (int)(origin.X + field.Bounds.Width) - 7; x++)
            {
                var index = y * stride + x * 4;
                if (bytes[index] > 180 && bytes[index + 1] > 180 && bytes[index + 2] > 180) { rows.Add(y); break; }
            }
            Assert.NotEmpty(rows);
            var delta = (rows.Min() + rows.Max() + 1) / 2d - (origin.Y + field.Bounds.Height / 2);
            var line = $"{context} {field.Name}: renderedInkRows={rows.Min()}..{rows.Max()}, inkCenterDelta={delta:F2}px"; Log(line);
            if (Math.Abs(delta) > 2) errors.Add(line);
        }
    }

    private static void Log(string text)
    {
        var directory = Environment.GetEnvironmentVariable("WYRM_TEST_SCREENSHOTS"); if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "alignment-measurements.txt"), text + Environment.NewLine);
    }
    private static T Field<T>(Window window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Render(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Capture(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("WYRM_TEST_SCREENSHOTS"); if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); Render(window); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }
}
