using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Platform;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

internal static class Localization
{
    private static LanguagePack? english;
    public static LanguagePack English
    {
        get { if (english is null) { using var reader = new StreamReader(AssetLoader.Open(new Uri("avares://Wyrmwatch/Assets/Languages/en.json"))); english = LanguagePack.Parse(reader.ReadToEnd()); } return english; }
    }
    private static Dictionary<string, string> active = [];
    public static string Key(string text) => "Ui." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
    public static string Text(string text) => active.GetValueOrDefault(Key(text), text);
    public static IReadOnlyList<LanguagePack> Available()
    {
        var result = new List<LanguagePack> { English };
        var folder = Path.Combine(Program.DataDirectory, "languages");
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").Take(50))
                try { SafePaths.NoLinks(file); if (new FileInfo(file).Length > 1_000_000) continue; var pack = LanguagePack.Parse(File.ReadAllText(file)); if (pack.Id != "en" && result.All(p => p.Id != pack.Id)) result.Add(pack); } catch (Exception error) when (error is IOException or ArgumentException or JsonException or UnauthorizedAccessException) { }
        return result;
    }
    public static void Apply(LanguagePack pack)
    {
        active = English.Strings.ToDictionary();
        foreach (var translation in pack.Strings.Where(p => active.ContainsKey(p.Key))) active[translation.Key] = translation.Value;
        foreach (var translation in active) Application.Current!.Resources[translation.Key] = translation.Value;
    }
}
