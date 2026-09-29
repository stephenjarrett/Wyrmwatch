using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dragonwilds.Core;

public sealed record AppRelease(string Version, string Notes, string Page, string Download, string Digest, long Bytes);
public sealed record PreparedUpdate(string Version, string Directory, string Executable);

public sealed class AppUpdates(Func<HttpClient>? createClient = null)
{
    public const string Repository = "stephenjarrett/Wyrmwatch";
    public const long DownloadLimit = 600L * 1024 * 1024;
    private static HttpClient Client()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Wyrmwatch/0.2.0");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
    public static AppRelease? ParseRelease(string json, Version current, string runtime)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var version) || version <= current) return null;
        var name = runtime == "win-x64" ? "Wyrmwatch-win-x64.zip" : "Wyrmwatch-linux-x64.tar.gz";
        var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == name);
        if (asset.ValueKind == JsonValueKind.Undefined) throw new IOException("The release does not contain a package for this platform.");
        var download = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!Uri.TryCreate(download, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal)) throw new IOException("The update download is not from the official repository.");
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)) throw new IOException("This release has no valid SHA-256 digest. Automatic download is unavailable.");
        var bytes = asset.GetProperty("size").GetInt64(); if (bytes <= 0 || bytes > DownloadLimit) throw new IOException("The release package has an unsupported size.");
        return new(version.ToString(), root.GetProperty("body").GetString() ?? "", $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}", download, digest[7..], bytes);
    }
    public async Task<AppRelease?> CheckAsync(Version current, string runtime, CancellationToken token = default)
    {
        using var client = createClient?.Invoke() ?? Client();
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(token), current, runtime);
    }
    public async Task<PreparedUpdate> DownloadAsync(AppRelease release, string destination, string runtime, IProgress<double>? progress = null, CancellationToken token = default)
    {
        SafePaths.NoLinks(destination); Directory.CreateDirectory(destination);
        var staging = Path.Combine(destination, $"{release.Version}-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        var package = Path.Combine(staging, runtime == "win-x64" ? "download.zip" : "download.tar.gz");
        using var client = createClient?.Invoke() ?? Client();
        var uri = new Uri(release.Download);
        HttpResponseMessage? response = null;
        try
        {
            for (var redirects = 0; redirects <= 4; redirects++)
            {
                if (uri.Scheme != "https" || uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new IOException("The download redirected to an untrusted host.");
                response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location) { uri = new Uri(uri, location); response.Dispose(); response = null; continue; }
                break;
            }
            if (response is null) throw new IOException("Too many download redirects.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > DownloadLimit) throw new IOException("The update is larger than the download limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += read; if (total > release.Bytes || total > DownloadLimit) throw new IOException("The package exceeds its advertised size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token); progress?.Report((double)total / release.Bytes * 100);
                }
                if (total != release.Bytes) throw new IOException("The update download was incomplete.");
            }
            return await VerifyAndExtractAsync(package, release.Digest, staging, runtime, release.Version, token);
        }
        finally { response?.Dispose(); }
    }
    public static async Task<PreparedUpdate> VerifyAndExtractAsync(string package, string digest, string staging, string runtime, string version, CancellationToken token = default)
    {
        SafePaths.NoLinks(package); SafePaths.NoLinks(staging);
        await using (var input = File.OpenRead(package))
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input, token)), digest, StringComparison.OrdinalIgnoreCase)) throw new IOException("The update checksum did not match. Nothing was installed.");
        var root = Path.Combine(staging, "unpacked");
        if (Directory.Exists(root)) throw new IOException("The staging destination already exists.");
        Directory.CreateDirectory(root); long total = 0; var entries = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        string Destination(string name, long length)
        {
            total += length;
            if (length < 0 || total > 2L * 1024 * 1024 * 1024 || entries.Count >= 10000 || !entries.Add(name)) throw new IOException("The update archive is too large or contains duplicate entries.");
            return SafePaths.Resolve(root, name.TrimEnd('/', '\\'));
        }
        if (runtime == "win-x64")
        {
            using var zip = ZipFile.OpenRead(package);
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new IOException("Linked files are not allowed in an update.");
                var target = Destination(entry.FullName, entry.Length);
                if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(target);
                else { await using var input = entry.Open(); await ExtractFileAsync(input, target, entry.Length, token); }
            }
        }
        else
        {
            await using var input = File.OpenRead(package); await using var gzip = new GZipStream(input, CompressionMode.Decompress); using var tar = new TarReader(gzip);
            while (await tar.GetNextEntryAsync(cancellationToken: token) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new IOException("The update contains an unsupported or linked entry.");
                var target = Destination(entry.Name, entry.Length);
                if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(target);
                else
                {
                    await ExtractFileAsync(entry.DataStream ?? Stream.Null, target, entry.Length, token);
                }
            }
        }
        var directory = Path.Combine(root, "Wyrmwatch-" + runtime);
        var executable = Path.Combine(directory, runtime == "win-x64" ? "Wyrmwatch.exe" : "Wyrmwatch");
        var agent = Path.Combine(directory, "agent", runtime == "win-x64" ? "Wyrmwatch.Agent.exe" : "Wyrmwatch.Agent");
        foreach (var required in new[] { executable, agent, Path.Combine(directory, "LICENSE"), Path.Combine(directory, "NOTICE"), Path.Combine(directory, "Wyrmwatch-source.zip"), Path.Combine(directory, "Wyrmwatch.runtimeconfig.json") })
            if (!File.Exists(required)) throw new IOException("The update is missing required application files.");
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); File.SetUnixFileMode(agent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        return new(version, directory, executable);
    }
    private static async Task ExtractFileAsync(Stream input, string target, long expected, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            total += read; if (total > expected) throw new IOException("An archive entry exceeds its advertised size.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (total != expected) throw new IOException("An archive entry is incomplete.");
    }
}
