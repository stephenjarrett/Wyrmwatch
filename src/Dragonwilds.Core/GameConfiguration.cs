using System.Text.RegularExpressions;

namespace Dragonwilds.Core;

public static partial class GameConfiguration
{
    public static readonly string[] Keys = ["OwnerId", "ServerName", "DefaultWorldName", "AdminPassword", "WorldPassword", "Port"];
    private static readonly string[] Sections = ["/Script/Dominion.DedicatedServerSettings", "/Script/RSDragonwilds.DedicatedServerConfig"];
    public static Dictionary<string, string> Read(string path) => File.Exists(path) ? ReadText(File.ReadAllText(path)) : new(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, string> ReadText(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var section = "";
        foreach (var line in text.Split('\n'))
        {
            var clean = line.Trim();
            if (clean.StartsWith('[') && clean.EndsWith(']')) { section = clean[1..^1]; continue; }
            var equals = clean.IndexOf('=');
            if (equals < 1 || !Sections.Contains(section, StringComparer.OrdinalIgnoreCase)) continue;
            var key = clean[..equals].Trim(); if (Keys.Contains(key, StringComparer.OrdinalIgnoreCase)) result[key] = clean[(equals + 1)..].Trim().Trim('"');
        }
        return result;
    }
    public static string Merge(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach (var pair in values)
        {
            if (!Keys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unsupported configuration field.");
            if (pair.Value.IndexOfAny(['\r', '\n', '\0', '"']) >= 0) throw new ArgumentException("Settings cannot contain line breaks or quotation marks.");
        }
        foreach (var required in new[] { "OwnerId", "ServerName", "DefaultWorldName", "AdminPassword" })
            if (!values.TryGetValue(required, out var value) || string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{required} is required.");
        if (!int.TryParse(values.GetValueOrDefault("Port", "7777"), out var port) || port is < 1024 or > 65535) throw new ArgumentException("Invalid port.");
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList(); var section = ""; var foundSection = false; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int lastIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var clean = lines[i].Trim();
            if (clean.StartsWith('[') && clean.EndsWith(']'))
            {
                if (Sections.Contains(section, StringComparer.OrdinalIgnoreCase))
                {
                    var missing = values.Where(p => !seen.Contains(p.Key)).Select(p => $"{p.Key}={p.Value}").ToArray();
                    lines.InsertRange(i, missing); i += missing.Length;
                }
                section = clean[1..^1]; seen.Clear();
                if (Sections.Contains(section, StringComparer.OrdinalIgnoreCase)) foundSection = true;
                continue;
            }
            if (!Sections.Contains(section, StringComparer.OrdinalIgnoreCase)) continue;
            lastIndex = i;
            var match = Assignment().Match(lines[i]);
            if (match.Success && values.TryGetValue(match.Groups[1].Value, out var value)) { lines[i] = $"{match.Groups[1].Value}={value}"; seen.Add(match.Groups[1].Value); }
        }
        if (Sections.Contains(section, StringComparer.OrdinalIgnoreCase)) lines.AddRange(values.Where(p => !seen.Contains(p.Key)).Select(p => $"{p.Key}={p.Value}"));
        if (!foundSection) { lines.Add($"[{Sections[0]}]"); lines.AddRange(values.Select(p => $"{p.Key}={p.Value}")); }
        return string.Join(newline, lines).TrimEnd('\r', '\n') + newline;
    }
    [GeneratedRegex(@"^\s*([A-Za-z][A-Za-z0-9]*)\s*=")]
    private static partial Regex Assignment();
}
