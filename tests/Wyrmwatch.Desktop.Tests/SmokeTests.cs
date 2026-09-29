using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
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
            var language = new Dragonwilds.Core.LanguagePack("xx", "Test language", new() { [Localization.Key("Make yourself at home.")] = "Translated settings" });
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
