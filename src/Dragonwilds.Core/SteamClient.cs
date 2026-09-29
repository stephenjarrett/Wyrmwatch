using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Formats.Tar;

namespace Dragonwilds.Core;

public static class CommandRunner
{
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        Action<string> log, TimeSpan timeout, CancellationToken token = default)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("The command could not be started.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        var captured = new StringBuilder(); var sync = new object();
        async Task Read(StreamReader reader)
        {
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                lock (sync) { if (captured.Length < 8_000_000) captured.AppendLine(line); }
                log(line);
            }
        }
        try
        {
            await Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError), process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}. See Activity for details.");
            return captured.ToString();
        }
        catch (OperationCanceledException)
        {
            // This process is the command we just started (never a game server discovered elsewhere).
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new IOException("The command was cancelled or exceeded its time limit. Inspect the operation before retrying.");
        }
    }
}

public static class SteamMetadata
{
    public static Dictionary<string, object> Parse(string text)
    {
        var tokens = Regex.Matches(text, "\"((?:\\\\.|[^\"\\\\])*)\"|[{}]")
            .Select(m => m.Value is "{" or "}" ? m.Value : m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")).ToArray();
        int offset = 0;
        Dictionary<string, object> Read()
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (offset < tokens.Length)
            {
                var key = tokens[offset++]; if (key == "}") break;
                if (offset >= tokens.Length) break;
                var value = tokens[offset++]; result[key] = value == "{" ? Read() : value;
            }
            return result;
        }
        return Read();
    }
    public static string? Value(Dictionary<string, object> tree, params string[] keys)
    {
        object node = tree;
        foreach (var key in keys)
        {
            if (node is not Dictionary<string, object> map || !map.TryGetValue(key, out var next)) return null;
            node = next;
        }
        return node as string;
    }
    public static string? PublicBuild(string text)
    {
        // SteamCMD also prints quoted diagnostics; begin at the actual app-info object.
        var start = Regex.Match(text, "\"4019830\"\\s*\\{");
        if (!start.Success) return null;
        var id = Value(Parse(text[start.Index..]), "4019830", "depots", "branches", "public", "buildid");
        return long.TryParse(id, out var number) && number > 0 ? id : null;
    }
}

public sealed class SteamClient(string toolsDirectory) : ISteamClient
{
    public string Executable => Path.Combine(toolsDirectory, "steamcmd", OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh");
    private readonly SemaphoreSlim gate = new(1, 1);
    public string? InstalledBuild(ServerProfile profile)
    {
        var candidates = new List<string> { Path.Combine(profile.InstallPath, "steamapps", "appmanifest_4019830.acf") };
        var parent = Directory.GetParent(profile.InstallPath);
        if (parent?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) == true)
            candidates.Add(Path.Combine(parent.Parent!.FullName, "appmanifest_4019830.acf"));
        foreach (var path in candidates.Where(File.Exists))
        {
            var tree = SteamMetadata.Parse(File.ReadAllText(path));
            if (SteamMetadata.Value(tree, "AppState", "appid") != "4019830") continue;
            if (path == candidates.Last() && candidates.Count > 1 && !string.Equals(SteamMetadata.Value(tree, "AppState", "installdir"), Path.GetFileName(SafePaths.Full(profile.InstallPath)), StringComparison.OrdinalIgnoreCase)) continue;
            var id = SteamMetadata.Value(tree, "AppState", "buildid");
            if (long.TryParse(id, out var number) && number > 0) return id;
        }
        return null;
    }
    public async Task<BuildStatus> CheckAsync(ServerProfile profile, Action<string> log, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            await EnsureAsync(log, token);
            var output = await CommandRunner.RunAsync(Executable, ["+login", "anonymous", "+app_info_update", "1", "+app_info_print", "4019830", "+quit"], Path.GetDirectoryName(Executable)!, log, TimeSpan.FromMinutes(5), token);
            return new(InstalledBuild(profile), SteamMetadata.PublicBuild(output));
        }
        finally { gate.Release(); }
    }
    public async Task InstallAsync(ServerProfile profile, bool repair, Action<string> log, CancellationToken token = default)
    {
        profile.Validate();
        await gate.WaitAsync(token);
        try
        {
            await EnsureAsync(log, token);
            var args = new List<string> { "+force_install_dir", profile.InstallPath, "+login", "anonymous", "+app_update", "4019830" };
            if (repair) args.Add("validate"); args.Add("+quit");
            var output = await CommandRunner.RunAsync(Executable, args, Path.GetDirectoryName(Executable)!, log, TimeSpan.FromMinutes(45), token);
            if (!output.Contains("Success! App '4019830' fully installed", StringComparison.OrdinalIgnoreCase)) throw new IOException("SteamCMD did not confirm a successful installation.");
            if (!File.Exists(profile.Launcher)) throw new IOException("SteamCMD finished, but the server launcher is missing.");
        }
        finally { gate.Release(); }
    }
    private async Task EnsureAsync(Action<string> log, CancellationToken token)
    {
        if (File.Exists(Executable)) return;
        var folder = Path.GetDirectoryName(Executable)!; Directory.CreateDirectory(folder); SafePaths.NoLinks(folder);
        log("Downloading SteamCMD from Valve…");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        if (OperatingSystem.IsWindows())
        {
            using var input = new MemoryStream(await client.GetByteArrayAsync("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip", token));
            using var zip = new ZipArchive(input);
            foreach (var entry in zip.Entries)
            {
                var target = SafePaths.Resolve(folder, entry.FullName);
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, true);
            }
        }
        else
        {
            using var input = new MemoryStream(await client.GetByteArrayAsync("https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz", token));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (await tar.GetNextEntryAsync(cancellationToken: token) is { } entry)
            {
                var name = entry.Name.StartsWith("./") ? entry.Name[2..] : entry.Name;
                if (string.IsNullOrEmpty(name)) continue;
                var target = SafePaths.Resolve(folder, name);
                if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new IOException("SteamCMD download contains an unsupported linked entry.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
                    if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, token);
                File.SetUnixFileMode(target, entry.Mode);
            }
        }
    }
}
