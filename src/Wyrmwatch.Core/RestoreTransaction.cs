using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wyrmwatch.Core;

public sealed record RestoreCoverage(bool IncludesWorld, string Description);

public sealed partial class BackupEngine
{
    internal Action<string>? RestoreCheckpoint { get; init; }
    internal sealed class RestoreInterruptedException : IOException { }
    private sealed record RestoreTransaction(string SavedPath, string Stage, string Previous,
        Dictionary<string, bool> Originals, Dictionary<string, bool> ExpectedFolders,
        List<BackupFile> OriginalFiles, List<BackupFile> ExpectedFiles, bool Committed = false);

    public static RestoreCoverage Coverage(BackupManifest manifest) =>
        manifest.Files.Any(f => f.Entry.StartsWith("SaveGames/", StringComparison.OrdinalIgnoreCase))
            ? new(true, "World and configuration: replaces SaveGames and Config completely. Newer files absent from this recovery point are retained separately.")
            : new(false, "Configuration only: replaces Config. Existing worlds in SaveGames are preserved.");

    private static string[] RestoreFolders(BackupManifest manifest) => Coverage(manifest).IncludesWorld ? folders : ["Config"];

    private static string JournalPath(ServerProfile profile)
    {
        var saved = SafePaths.Full(profile.SavedPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? saved.ToUpperInvariant() : saved)));
        return Path.Combine(Path.GetDirectoryName(saved)!, ".wyrmwatch-restore-" + key + ".json");
    }

    // Presence alone blocks even corrupt/unreadable journals. Do not silently fall back.
    public bool HasPendingRestore(ServerProfile profile) => JournalExists(JournalPath(profile)) || JournalExists(JournalPath(profile) + ".bak");
    private static bool JournalExists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
    }
    public void EnsureNoPendingRestore(ServerProfile profile)
    {
        if (HasPendingRestore(profile)) throw new IOException("An interrupted restore needs recovery. Keep the server stopped and use Recover interrupted restore before starting or changing files.");
    }

    private static void WriteTransaction(ServerProfile profile, RestoreTransaction transaction)
    {
        var document = JsonSerializer.Serialize(transaction, JsonStore.Options);
        if (Encoding.UTF8.GetByteCount(document) > 32 * 1024 * 1024) throw new IOException("Restore recovery inventory is too large. Active files were preserved.");
        AtomicFile.Write(JournalPath(profile), document);
    }
    private static void DeleteTransaction(ServerProfile profile)
    {
        var path = JournalPath(profile);
        SafePaths.NoLinks(path); SafePaths.NoLinks(path + ".bak");
        // Remove the fallback first: a kill during cleanup still leaves the committed
        // primary journal blocking until its inventory has been verified again.
        File.Delete(path + ".bak");
        File.Delete(path);
    }

    private static RestoreTransaction ReadTransaction(ServerProfile profile)
    {
        try { return ReadTransactionCore(profile); }
        catch (ArgumentException error) { throw new IOException("Restore recovery journal paths are invalid. Files were preserved.", error); }
    }

    private static RestoreTransaction ReadTransactionCore(ServerProfile profile)
    {
        var path = JournalPath(profile);
        if (!JournalExists(path)) path += ".bak";
        SafePaths.NoLinks(path);
        RestoreTransaction transaction;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 32 * 1024 * 1024) throw new IOException("Restore recovery journal is too large. Files were preserved; inspect it before recovery.");
            transaction = JsonSerializer.Deserialize<RestoreTransaction>(File.ReadAllText(path), JsonStore.Options)
                ?? throw new JsonException("Empty recovery journal.");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or UnauthorizedAccessException) { throw new IOException("Restore recovery journal is unreadable. Files were preserved; inspect it before recovery.", error); }
        var parent = Path.GetDirectoryName(SafePaths.Full(profile.SavedPath))!;
        if (string.IsNullOrWhiteSpace(transaction.SavedPath) || string.IsNullOrWhiteSpace(transaction.Stage) || string.IsNullOrWhiteSpace(transaction.Previous) ||
            !Path.IsPathFullyQualified(transaction.Stage) || !Path.IsPathFullyQualified(transaction.Previous) ||
            !SafePaths.Same(transaction.SavedPath, profile.SavedPath) ||
            transaction.Originals is null || transaction.Originals.Count is < 1 or > 2 ||
            transaction.ExpectedFolders is null || !transaction.Originals.Keys.Order().SequenceEqual(transaction.ExpectedFolders.Keys.Order()) ||
            transaction.OriginalFiles is null || transaction.ExpectedFiles is null ||
            transaction.Originals.Keys.Any(f => !folders.Contains(f, StringComparer.Ordinal)) ||
            Path.GetDirectoryName(transaction.Stage) is null || !SafePaths.Same(Path.GetDirectoryName(transaction.Stage)!, parent) || !Path.GetFileName(transaction.Stage).StartsWith(".restore-", StringComparison.Ordinal) ||
            Path.GetDirectoryName(transaction.Previous) is null || !SafePaths.Same(Path.GetDirectoryName(transaction.Previous)!, parent) || !Path.GetFileName(transaction.Previous).StartsWith(".recovery-", StringComparison.Ordinal) ||
            SafePaths.Same(transaction.Stage, transaction.Previous))
            throw new IOException("Restore recovery journal paths are invalid. Files were preserved.");
        SafePaths.NoLinks(transaction.Stage); SafePaths.NoLinks(transaction.Previous); SafePaths.NoLinks(profile.SavedPath);
        foreach (var file in transaction.OriginalFiles.Concat(transaction.ExpectedFiles))
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Entry) || file.Length < 0 || file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(char.IsAsciiHexDigit) ||
                !transaction.Originals.ContainsKey(file.Entry.Split('/')[0]))
                throw new IOException("Restore recovery journal inventory is invalid. Files were preserved.");
            SafePaths.Resolve(profile.SavedPath, file.Entry);
        }
        return transaction;
    }

    private static async Task<List<BackupFile>> InventoryAsync(ServerProfile profile, IEnumerable<string> covered, CancellationToken token)
    {
        var inventory = new List<BackupFile>();
        foreach (var source in SourceFiles(profile))
        {
            var relative = Path.GetRelativePath(profile.SavedPath, source).Replace('\\', '/');
            if (!covered.Contains(relative.Split('/')[0], StringComparer.Ordinal)) continue;
            await using var stream = File.OpenRead(source);
            inventory.Add(new(relative, stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), File.GetLastWriteTimeUtc(source)));
        }
        return inventory;
    }

    private static async Task VerifyActiveAsync(ServerProfile profile, RestoreTransaction transaction, CancellationToken token)
    {
        var expectedFolders = transaction.Committed ? transaction.ExpectedFolders : transaction.Originals;
        foreach (var (folder, exists) in expectedFolders)
            if (Directory.Exists(Path.Combine(profile.SavedPath, folder)) != exists)
                throw new IOException("Recovery cannot verify the expected folders. The recovery journal and all files were preserved; starting remains blocked.");
        var actual = await InventoryAsync(profile, expectedFolders.Keys, token);
        var expected = transaction.Committed ? transaction.ExpectedFiles : transaction.OriginalFiles;
        var signatures = (List<BackupFile> files) => files.Select(f => $"{f.Entry}|{f.Length}|{f.Sha256.ToUpperInvariant()}").Order(StringComparer.Ordinal);
        if (!signatures(actual).SequenceEqual(signatures(expected), StringComparer.Ordinal))
            throw new IOException("Recovery integrity verification failed. The recovery journal and all files were preserved; starting remains blocked.");
    }

    public async Task<string> RecoverInterruptedRestoreAsync(ServerProfile profile, Func<Task<bool>> isStopped, CancellationToken token = default)
    {
        profile.Validate();
        if (!HasPendingRestore(profile)) return "No interrupted restore needs recovery.";
        if (!await isStopped()) throw new IOException("Stop the server before recovering an interrupted restore.");
        var transaction = ReadTransaction(profile);
        token.ThrowIfCancellationRequested();
        if (!transaction.Committed)
        {
            Directory.CreateDirectory(profile.SavedPath);
            foreach (var (folder, existed) in transaction.Originals)
            {
                if (!await isStopped()) throw new IOException("The server started during recovery. Files and the recovery journal were preserved; stop it before retrying.");
                var destination = Path.Combine(profile.SavedPath, folder);
                var original = Path.Combine(transaction.Previous, folder);
                var incoming = Path.Combine(transaction.Stage, folder);
                var displaced = Path.Combine(transaction.Previous, "interrupted-" + folder);
                foreach (var path in new[] { destination, original, incoming, displaced }) SafePaths.NoLinks(path);
                // An original in recovery proves its move completed. If there never
                // was an original, an absent staged folder proves install completed.
                if (Directory.Exists(original) || (!existed && !Directory.Exists(incoming) && Directory.Exists(destination)))
                {
                    if (Directory.Exists(destination))
                    {
                        if (Directory.Exists(displaced) || File.Exists(displaced)) throw new IOException("Recovery destination is occupied. Files were preserved; resolve it before retrying recovery.");
                        Directory.Move(destination, displaced);
                        RestoreCheckpoint?.Invoke("RecoveryRetained:" + folder);
                    }
                    if (Directory.Exists(original))
                    {
                        Directory.Move(original, destination);
                        RestoreCheckpoint?.Invoke("RecoveryRestored:" + folder);
                    }
                }
            }
        }
        await VerifyActiveAsync(profile, transaction, token);
        DeleteTransaction(profile);
        return transaction.Committed
            ? $"Completed restore verified. Previous files retained in {transaction.Previous}. The server remains stopped."
            : $"Interrupted restore rolled back. Staged and displaced files retained in {transaction.Stage} and {transaction.Previous}. The server remains stopped.";
    }
}
