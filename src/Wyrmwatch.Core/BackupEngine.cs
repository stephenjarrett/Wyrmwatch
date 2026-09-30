using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wyrmwatch.Core;

public sealed class BackupEngine
{
    // Persisted archive-format identifier: retain compatibility with existing recovery points.
    public const string Product = "Dragonwilds.NativeManager";
    private static readonly string[] folders = ["SaveGames", "Config"];
    public async Task<BackupInfo> CreateAsync(ServerProfile profile, string reason, bool running, CancellationToken token = default)
    {
        profile.Validate();
        SafePaths.NoLinks(profile.SavedPath);
        var files = SourceFiles(profile).ToArray();
        if (files.Length == 0) throw new IOException("No save or configuration files were found. No backup was created.");
        Directory.CreateDirectory(profile.BackupPath);
        var target = Path.Combine(profile.BackupPath, $"dw-{profile.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        var partial = target + ".partial";
        var inventory = new List<BackupFile>();
        try
        {
            await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    foreach (var source in files)
                    {
                        token.ThrowIfCancellationRequested(); SafePaths.NoLinks(source);
                        var before = new FileInfo(source);
                        var length = before.Length; var written = before.LastWriteTimeUtc;
                        var relative = Path.GetRelativePath(profile.SavedPath, source).Replace('\\', '/');
                        var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                        entry.LastWriteTime = new DateTimeOffset(written < new DateTime(1980, 1, 1) ? new DateTime(1980, 1, 1) : written);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true))
                        await using (var output = entry.Open())
                        {
                            var buffer = new byte[81920]; int read;
                            while ((read = await input.ReadAsync(buffer, token)) > 0) { hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token); }
                        }
                        before.Refresh();
                        if (before.Length != length || before.LastWriteTimeUtc != written) throw new IOException("A game file changed during backup. Retry after the next save completes.");
                        inventory.Add(new(relative, length, Convert.ToHexString(hash.GetHashAndReset()), written));
                    }
                    var manifest = new BackupManifest(1, Product, SafePaths.Full(profile.InstallPath), profile.Id, SafePaths.Full(profile.SavedPath), DateTimeOffset.UtcNow, reason, running, inventory);
                    await using var manifestStream = zip.CreateEntry("manifest.json").Open();
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonStore.Options, token);
                }
                await stream.FlushAsync(token); stream.Flush(true);
            }
            // Recheck the entire source set after the archive is closed, not just one file at a time.
            var afterFiles = SourceFiles(profile).ToArray();
            if (!files.SequenceEqual(afterFiles, StringComparer.OrdinalIgnoreCase)) throw new IOException("The save file set changed during backup.");
            foreach (var file in inventory)
            {
                var source = SafePaths.Resolve(profile.SavedPath, file.Entry);
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != file.Sha256) throw new IOException("Game data changed during backup. The incomplete archive was discarded.");
            }
            await VerifyAsync(partial, profile, token);
            File.Move(partial, target);
            var result = ReadInfo(target)!;
            return result;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    public void Prune(ServerProfile profile)
    {
        // Only our completed archives belonging to this profile and this exact installation are eligible.
        foreach (var old in List(profile).Skip(profile.RetainBackups))
        {
            SafePaths.NoLinks(old.Path);
            File.Delete(old.Path);
        }
    }
    private static IEnumerable<string> SourceFiles(ServerProfile profile)
    {
        var result = new List<string>();
        foreach (var folder in folders)
        {
            var path = Path.Combine(profile.SavedPath, folder);
            if (!Directory.Exists(path)) continue;
            Walk(path, result);
        }
        return result.Order(StringComparer.OrdinalIgnoreCase);
    }
    private static void Walk(string folder, List<string> result)
    {
        SafePaths.NoLinks(folder);
        foreach (var file in Directory.EnumerateFiles(folder)) { SafePaths.NoLinks(file); result.Add(file); }
        foreach (var child in Directory.EnumerateDirectories(folder)) Walk(child, result);
    }
    public IReadOnlyList<BackupInfo> List(ServerProfile profile)
    {
        if (!Directory.Exists(profile.BackupPath)) return [];
        SafePaths.NoLinks(profile.BackupPath);
        return Directory.EnumerateFiles(profile.BackupPath, $"dw-{profile.Id}-*.zip").Select(ReadInfo)
            .OfType<BackupInfo>().Where(b => b.Manifest.ProfileId == profile.Id && SafePaths.Same(b.Manifest.Installation, profile.InstallPath) && SafePaths.Same(b.Manifest.DataRoot, profile.SavedPath))
            .OrderByDescending(b => b.Manifest.Created).ToArray();
    }
    public static BackupInfo? ReadInfo(string path)
    {
        try
        {
            SafePaths.NoLinks(path);
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("manifest.json");
            if (entry is null || entry.Length > 8_000_000) return null;
            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<BackupManifest>(stream, JsonStore.Options);
            return manifest is { Product: Product, Format: 1, Files.Count: > 0 } && !string.IsNullOrEmpty(manifest.Installation) && !string.IsNullOrEmpty(manifest.DataRoot) && !string.IsNullOrEmpty(manifest.ProfileId)
                ? new(path, manifest, new FileInfo(path).Length) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    public async Task<BackupManifest> VerifyAsync(string path, ServerProfile profile, CancellationToken token = default)
    {
        var info = ReadInfo(path) ?? throw new IOException("This is not a supported backup archive.");
        if (info.Manifest.ProfileId != profile.Id || !SafePaths.Same(info.Manifest.Installation, profile.InstallPath)) throw new IOException("This backup belongs to a different server.");
        if (!SafePaths.Same(info.Manifest.DataRoot, profile.SavedPath)) throw new IOException("This backup uses a different save-data folder. Check your server connection before restoring.");
        using var zip = ZipFile.OpenRead(path);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (info.Manifest.Files.Count == 0 || zip.Entries.Count != info.Manifest.Files.Count + 1) throw new IOException("Archive inventory is incomplete.");
        foreach (var file in info.Manifest.Files)
        {
            SafePaths.Resolve(profile.SavedPath, file.Entry);
            var parts = file.Entry.Split('/');
            if (parts.Length < 2 || !folders.Contains(parts[0], StringComparer.OrdinalIgnoreCase) || !seen.Add(file.Entry)) throw new IOException("Unexpected or duplicate archive path.");
            var entry = zip.GetEntry(file.Entry) ?? throw new IOException("A backup file is missing.");
            if (entry.Length != file.Length) throw new IOException("A backup file has the wrong size.");
            await using var input = entry.Open();
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input, token)), file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Backup integrity check failed.");
        }
        return info.Manifest;
    }
    public async Task<string> RestoreAsync(ServerProfile profile, string archive, Func<Task<bool>> isStopped, CancellationToken token = default)
    {
        profile.Validate();
        var manifest = await VerifyAsync(archive, profile, token);
        if (!await isStopped()) throw new IOException("Stop the server before restoring a backup.");
        await CreateAsync(profile, "Before restore", false, token);
        var parent = Path.GetDirectoryName(SafePaths.Full(profile.SavedPath))!;
        var stage = Path.Combine(parent, ".restore-" + Guid.NewGuid().ToString("N"));
        var previous = Path.Combine(parent, ".recovery-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage); Directory.CreateDirectory(previous);
        using (var zip = ZipFile.OpenRead(archive))
        {
            foreach (var file in manifest.Files)
            {
                var path = SafePaths.Resolve(stage, file.Entry);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var input = zip.GetEntry(file.Entry)!.Open())
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { await input.CopyToAsync(output, token); await output.FlushAsync(token); }
                await using var staged = File.OpenRead(path);
                if (Convert.ToHexString(await SHA256.HashDataAsync(staged, token)) != file.Sha256) throw new IOException("Staged restore integrity failed. Active game files were not changed.");
            }
        }
        if (!await isStopped()) throw new IOException("The server started during restore preparation. Nothing was replaced.");
        token.ThrowIfCancellationRequested();
        // Move whole directories so newer saves cannot silently win over the restored world.
        // Originals are retained in recovery, and rollback preserves all staged data on any failure.
        var moved = new List<string>(); var installed = new List<string>();
        try
        {
            foreach (var folder in folders)
            {
                var incoming = Path.Combine(stage, folder);
                if (!Directory.Exists(incoming)) continue;
                var destination = Path.Combine(profile.SavedPath, folder);
                SafePaths.NoLinks(destination); Directory.CreateDirectory(profile.SavedPath);
                if (Directory.Exists(destination)) { Directory.Move(destination, Path.Combine(previous, folder)); moved.Add(folder); }
                Directory.Move(incoming, destination); installed.Add(folder);
            }
        }
        catch
        {
            foreach (var folder in installed) Directory.Move(Path.Combine(profile.SavedPath, folder), Path.Combine(stage, folder));
            foreach (var folder in moved) Directory.Move(Path.Combine(previous, folder), Path.Combine(profile.SavedPath, folder));
            throw;
        }
        return previous;
    }
}
