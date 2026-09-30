using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Wyrmwatch.Core;
using Wyrmwatch.Desktop;

[assembly: AvaloniaTestApplication(typeof(Wyrmwatch.Desktop.Tests.TestApp))]

namespace Wyrmwatch.Desktop.Tests;

public class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
public class SmokeTests
{
    [AvaloniaFact]
    public void SelectedNavigationRemainsReadableAcrossThemeChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        Program.HeadlessTest = true; Program.Demo = false; Program.DataDirectory = root;
        var originalTheme = Application.Current!.RequestedThemeVariant;
        var window = new MainWindow(); window.Show();
        try
        {
            var nav = window.FindControl<ListBox>("Navigation")!;
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = theme;
                nav.SelectedIndex = 5;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var selected = Assert.IsType<ListBoxItem>(nav.SelectedItem);
                var presenter = selected.GetVisualDescendants().OfType<ContentPresenter>().First();
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color;
                foreach (var label in selected.GetLogicalDescendants().OfType<TextBlock>())
                {
                    var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color;
                    var first = Luminance(foreground); var second = Luminance(background);
                    var contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
                    Assert.True(contrast >= 4.5, $"{theme} navigation contrast was {contrast:F2}:1.");
                }
            }
        }
        finally { window.Close(); Application.Current.RequestedThemeVariant = originalTheme; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte value) { var channel = value / 255d; return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    [AvaloniaFact]
    public async Task ImportRequiresFolderReviewAndConfirmationAfterEveryChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        var launcher = Path.Combine(root, "RSDragonwildsServer.exe");
        var saved = Path.Combine(root, "RSDragonwilds", "Saved"); Directory.CreateDirectory(saved); File.WriteAllText(launcher, "fixture");
        var dialog = new ImportServerDialog(launcher, []);
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        try
        {
            await dialog.ReviewAsync();
            var controls = dialog.GetLogicalDescendants().OfType<Control>().ToArray();
            var import = controls.OfType<Button>().Single(b => Equals(b.Content, "Import server"));
            var confirmation = controls.OfType<CheckBox>().Single();
            Assert.False(import.IsEnabled); confirmation.IsChecked = true;
            Assert.True(import.IsEnabled, string.Join("\n", controls.OfType<TextBlock>().Select(t => t.Text)));
            controls.OfType<TextBox>().Single(t => t.Name == "ImportSavedFolder").Text = Path.Combine(root, "missing");
            Assert.False(import.IsEnabled); Assert.False(confirmation.IsChecked);
            await dialog.ReviewAsync(); confirmation.IsChecked = true; Assert.False(import.IsEnabled);
            Assert.Equal("fixture", File.ReadAllText(launcher));
        }
        finally { dialog.Close(); if (SafePaths.Within(root, Path.GetTempPath())) Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public void PagesThemesAndFocusedFormSurviveStatusUpdates()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        Program.HeadlessTest = true; Program.Demo = true; Program.DataDirectory = root;
        var window = new MainWindow(); window.Show();
        try
        {
            Assert.IsType<WorkspaceModel>(window.DataContext);
            var nav = window.FindControl<ListBox>("Navigation")!;
            for (var i = 0; i < nav.ItemCount; i++) { nav.SelectedIndex = i; Assert.False(string.IsNullOrWhiteSpace(((WorkspaceModel)window.DataContext!).PageTitle)); }
            nav.SelectedIndex = 5;
            var field = window.FindControl<TextBox>("ProfileName")!; field.Text = "Unsaved user input"; field.Focus();
            var model = (WorkspaceModel)window.DataContext!; model.Cpu = "12.3%"; model.Players = "3"; model.Status = "Online";
            Assert.Equal("Unsaved user input", field.Text);
            var language = new Wyrmwatch.Core.LanguagePack("xx", "Test language", new() { [Localization.Key("Make yourself at home.")] = "Translated settings" });
            var languages = window.FindControl<ComboBox>("LanguagePicker")!; languages.ItemsSource = new[] { Localization.English, language }; languages.SelectedIndex = 1;
            Assert.Equal("Translated settings", model.PageTitle); Assert.Equal("Unsaved user input", field.Text);
            languages.SelectedIndex = 0;
            window.FindControl<ComboBox>("ThemePicker")!.SelectedIndex = 1;
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
            nav.SelectedIndex = 0; nav.SelectedIndex = 5; Assert.Equal("Unsaved user input", field.Text);
        }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public void FirstRunHasNoConnectedServerOrEnabledActions()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-" + Guid.NewGuid().ToString("N"));
        Program.HeadlessTest = true; Program.Demo = false; Program.DataDirectory = root;
        var window = new MainWindow(); window.Show();
        try { var model = Assert.IsType<WorkspaceModel>(window.DataContext); Assert.Empty(model.Profiles); Assert.True(model.NoServer); Assert.False(model.CanAct); }
        finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
