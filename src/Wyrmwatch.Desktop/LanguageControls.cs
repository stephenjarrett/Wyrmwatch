using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Dragonwilds.Core;

namespace Wyrmwatch.Desktop;

public partial class MainWindow
{
    private async Task LoadLanguagesAsync(string id)
    {
        try
        {
            var languages = await Task.Run(Localization.Available); LanguagePicker.ItemsSource = languages;
            LanguagePicker.SelectedItem = languages.FirstOrDefault(l => l.Id == id) ?? languages[0];
        }
        catch (Exception error) { model.Notice = "Language packs: " + error.Message; }
    }
    private void LanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ready || LanguagePicker.SelectedItem is not LanguagePack pack) return;
        Localization.Apply(pack); Navigate(null, null!);
    }
    private async void ImportLanguage(object? sender, RoutedEventArgs e)
    {
        if (Program.Demo || Program.HeadlessTest) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Import a community language pack", AllowMultiple = false, FileTypeFilter = [new("Language pack") { Patterns = ["*.json"] }] });
            var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
            var pack = await Task.Run(() => { if (new FileInfo(path).Length > 1_000_000) throw new IOException("The language pack is too large."); return LanguagePack.Parse(File.ReadAllText(path)); });
            if (pack.Id == "en") throw new ArgumentException("Choose a language ID other than en; English is the built-in fallback.");
            var destination = Path.Combine(Program.DataDirectory, "languages", pack.Id + ".json");
            if (File.Exists(destination) && !await Confirm("Replace this language pack?", "The previous pack will be retained as a .bak file.", "Replace pack")) return;
            await Task.Run(() => AtomicFile.Write(destination, JsonSerializer.Serialize(pack, JsonStore.Options)));
            await LoadLanguagesAsync(pack.Id); model.Notice = "Language pack imported. Save desktop preferences to keep this selection.";
        }
        catch (Exception error) { model.Notice = error.Message; }
    }
    private async void ExportLanguage(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Export the language template", SuggestedFileName = "wyrmwatch-language-template.json", DefaultExtension = "json" });
            var path = file?.TryGetLocalPath(); if (path is null) return;
            var pack = Localization.English with { Id = "xx", Name = "My language" };
            await Task.Run(() => AtomicFile.Write(path, JsonSerializer.Serialize(pack, JsonStore.Options))); model.Notice = "Template exported. Translate the values while preserving the keys, then import it.";
        }
        catch (Exception error) { model.Notice = error.Message; }
    }
}
