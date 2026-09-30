using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wyrmwatch.Core;

public sealed record LanguagePack(string Id, string Name, Dictionary<string, string> Strings)
{
    public static LanguagePack Parse(string json)
    {
        if (json.Length > 1_000_000) throw new ArgumentException("The language pack is too large.");
        var pack = JsonSerializer.Deserialize<LanguagePack>(json, new JsonSerializerOptions(JsonStore.Options) { MaxDepth = 5 }) ?? throw new ArgumentException("The language pack is empty.");
        if (pack.Id is null || !Regex.IsMatch(pack.Id, "^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$") || string.IsNullOrWhiteSpace(pack.Name) || pack.Name.Length > 60 || pack.Strings is null || pack.Strings.Count > 1000) throw new ArgumentException("Invalid language-pack metadata.");
        if (pack.Strings.Any(p => !Regex.IsMatch(p.Key, "^Ui\\.[a-f0-9]{12}$") || p.Value is null || p.Value.Length > 4000)) throw new ArgumentException("Invalid translation key or value.");
        return pack;
    }
}
