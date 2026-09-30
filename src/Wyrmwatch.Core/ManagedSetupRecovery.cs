using System.Security.Cryptography;
using System.Text.Json;

namespace Wyrmwatch.Core;

public sealed record PreparedSetupCandidate(string InstallPath, string Name, string StorageName, string Kind, string OriginalId);
public sealed record RecoveredSetup(ServerProfile Profile, Dictionary<string, string> Configuration,
    WorldImportPlan? Source, string Kind, string ReceiptToken);

/// <summary>Explicit recovery of this workspace's prepared setup; never adopts an unowned directory.</summary>
public static class ManagedSetupRecovery
{
    public const string ReceiptFileName = ".wyrmwatch-setup.json";
    private const string RegistryFileName = "pending-setups.json";
    private const string Product = "Wyrmwatch.ManagedSetup";
    private const int MaximumDocumentBytes = 8 * 1024 * 1024;
    private const int MaximumInventoryEntries = 100_000;
    private const int MaximumPendingSetups = 2_000;
    private const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumTotalBytes = 8L * 1024 * 1024 * 1024;
    private static readonly object registryLock = new();
    private sealed record Ownership(string ReceiptId, string InstallPath, string ReceiptToken);
    private sealed record Receipt(int Format, string Product, string ReceiptId, ServerProfile Profile, string Kind,
        WorldImportPlan? Source, List<BackupFile> Files, List<string> SavedDirectories);

    // Presence is only an indicator. The workspace registry and full file inventory
    // must both verify before the app offers to resume registration.
    public static bool HasReceipt(string installPath)
    {
        try { File.GetAttributes(Path.Combine(installPath, ReceiptFileName)); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static async Task WriteAsync(ServerProfile finalProfile, ServerProfile stagedProfile, WorldImportPlan? source,
        JsonStore owner, CancellationToken token = default)
    {
        ValidateManaged(finalProfile); ValidateManaged(stagedProfile);
        if (finalProfile.Id != stagedProfile.Id || finalProfile.Name != stagedProfile.Name || finalProfile.Port != stagedProfile.Port ||
            !SafePaths.Same(finalProfile.BackupPath, stagedProfile.BackupPath) || SafePaths.Same(finalProfile.InstallPath, stagedProfile.InstallPath))
            throw new IOException("Prepared setup identity does not match its staged installation.");
        var inventory = await InventoryAsync(stagedProfile, token);
        ValidateConfiguration(stagedProfile);
        ValidateSourceSnapshot(stagedProfile, source, inventory.Files);
        var receipt = new Receipt(1, Product, Guid.NewGuid().ToString("N"), finalProfile, source is null ? "create" : "import",
            source, inventory.Files, inventory.Directories);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonStore.Options);
        if (bytes.Length > MaximumDocumentBytes) throw new IOException("Prepared setup receipt is too large. Downloaded files remain in staging.");
        var path = Path.Combine(stagedProfile.InstallPath, ReceiptFileName); SafePaths.NoLinks(path);
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            { await file.WriteAsync(bytes, token); await file.FlushAsync(token); file.Flush(true); }
            File.Move(partial, path);
            // Durable owner proof precedes publication of the staging directory.
            // A marker copied/fabricated elsewhere is insufficient to resume setup.
            lock (registryLock)
            {
                var registry = ReadRegistry(owner);
                if (registry.Count >= MaximumPendingSetups) throw new IOException("Too many retained setup receipts. Review pending setups before creating another.");
                registry[receipt.ReceiptId] = new(receipt.ReceiptId, SafePaths.Full(finalProfile.InstallPath), Hash(bytes));
                owner.Write(RegistryFileName, registry);
            }
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public static IReadOnlyList<PreparedSetupCandidate> ListCandidates(JsonStore owner, int maximum = 100)
    {
        if (maximum is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximum));
        Dictionary<string, Ownership> registry; lock (registryLock) registry = ReadRegistry(owner);
        var candidates = new List<PreparedSetupCandidate>();
        foreach (var entry in registry.Values.Take(maximum))
        {
            if (candidates.Count >= maximum) break;
            if (!Directory.Exists(entry.InstallPath)) continue; // Not yet published.
            try
            {
                var (receipt, _) = ReadOwned(entry.InstallPath, owner);
                if (!candidates.Any(p => SafePaths.Same(p.InstallPath, receipt.Profile.InstallPath)))
                    candidates.Add(new(receipt.Profile.InstallPath, receipt.Profile.Name, Path.GetFileName(receipt.Profile.InstallPath), receipt.Kind, receipt.Profile.Id));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { /* Invalid candidates require investigation; they are never offered as resumable. */ }
        }
        return candidates;
    }

    public static async Task<RecoveredSetup> VerifyAsync(string installPath, JsonStore owner, CancellationToken token = default)
    {
        var (receipt, receiptToken) = ReadOwned(installPath, owner);
        var actual = await InventoryAsync(receipt.Profile, token);
        var expectedSignatures = Signatures(receipt.Files);
        if (!Signatures(actual.Files).SequenceEqual(expectedSignatures, StringComparer.Ordinal) ||
            !actual.Directories.Select(NormalizeEntry).Order(StringComparer.Ordinal).SequenceEqual(receipt.SavedDirectories.Select(NormalizeEntry).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new IOException("Prepared setup files changed. Existing files were preserved; this setup cannot be resumed automatically.");
        var configuration = ValidateConfiguration(receipt.Profile);
        ValidateSourceSnapshot(receipt.Profile, receipt.Source, receipt.Files);
        return new(receipt.Profile, configuration, receipt.Source, receipt.Kind, receiptToken);
    }

    public static void ClearAfterRegistration(ServerProfile profile, JsonStore owner)
    {
        ValidateManaged(profile);
        var path = Path.Combine(profile.InstallPath, ReceiptFileName); SafePaths.NoLinks(path);
        File.Delete(path);
        lock (registryLock)
        {
            var registry = ReadRegistry(owner);
            foreach (var key in registry.Where(pair => SafePaths.Same(pair.Value.InstallPath, profile.InstallPath)).Select(pair => pair.Key).ToArray()) registry.Remove(key);
            owner.Write(RegistryFileName, registry);
        }
    }

    private static (Receipt Receipt, string Token) ReadOwned(string installPath, JsonStore owner)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Path.IsPathFullyQualified(installPath)) throw new IOException("Choose the full path to a prepared setup.");
        var root = SafePaths.Full(installPath); SafePaths.NoLinks(root);
        Dictionary<string, Ownership> registry; lock (registryLock) registry = ReadRegistry(owner);
        var entries = registry.Values.Where(entry => SafePaths.Same(entry.InstallPath, root)).ToArray();
        if (entries.Length == 0) throw new IOException("This workspace does not own a prepared setup at this location. Existing files were preserved.");
        var path = Path.Combine(root, ReceiptFileName); SafePaths.NoLinks(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumDocumentBytes) throw new IOException("Prepared setup receipt is missing, incomplete or oversized.");
        var bytes = ReadDocument(path); var hash = Hash(bytes);
        if (bytes.Length > MaximumDocumentBytes) throw new IOException("Prepared setup receipt is oversized.");
        entries = entries.Where(entry => entry.ReceiptToken == hash).ToArray();
        if (entries.Length != 1) throw new IOException("Prepared setup receipt changed or belongs to another workspace. Existing files were preserved.");
        Receipt receipt;
        try { receipt = JsonSerializer.Deserialize<Receipt>(bytes, JsonStore.Options) ?? throw new JsonException("Empty receipt."); }
        catch (JsonException error) { throw new IOException("Prepared setup receipt is unreadable. Existing files were preserved.", error); }
        if (receipt.Format != 1 || receipt.Product != Product || receipt.ReceiptId != entries[0].ReceiptId || receipt.Profile is null ||
            receipt.Kind is not "create" and not "import" || (receipt.Kind == "import") != (receipt.Source is not null) ||
            receipt.Files is null || receipt.SavedDirectories is null || receipt.Files.Count is < 2 or > MaximumInventoryEntries || receipt.SavedDirectories.Count > MaximumInventoryEntries)
            throw new IOException("Prepared setup receipt identity or inventory is invalid.");
        ValidateManaged(receipt.Profile);
        if (!SafePaths.Same(receipt.Profile.InstallPath, root)) throw new IOException("Prepared setup receipt targets a different installation.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in receipt.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Entry) || file.Length < 0 || !ValidHash(file.Sha256) || !seen.Add(NormalizeEntry(file.Entry)))
                throw new IOException("Prepared setup receipt file inventory is invalid.");
            var resolved = SafePaths.Resolve(root, file.Entry);
            if (!SafePaths.Within(resolved, receipt.Profile.SavedPath) && !CriticalPaths(receipt.Profile).Any(p => SafePaths.Same(p, resolved)))
                throw new IOException("Prepared setup receipt covers an unexpected installation file.");
        }
        foreach (var directory in receipt.SavedDirectories)
            if (string.IsNullOrWhiteSpace(directory) || !SafePaths.Within(SafePaths.Resolve(root, directory), receipt.Profile.SavedPath))
                throw new IOException("Prepared setup receipt directory inventory is invalid.");
        return (receipt, hash);
    }

    private static void ValidateManaged(ServerProfile profile)
    {
        profile.Validate();
        if (!string.IsNullOrEmpty(profile.DataPath) || !string.IsNullOrEmpty(profile.LauncherPath) || profile.AutoBackup || profile.AutoUpdate)
            throw new IOException("Prepared setup must use its own managed folders with automation disabled.");
    }

    private static Dictionary<string, string> ValidateConfiguration(ServerProfile profile)
    {
        if (!File.Exists(profile.ConfigPath)) throw new IOException("Prepared setup configuration is missing.");
        if (new FileInfo(profile.ConfigPath).Length > 2 * 1024 * 1024) throw new IOException("Prepared setup configuration is oversized.");
        var values = GameConfiguration.Read(profile.ConfigPath);
        if (string.IsNullOrWhiteSpace(values.GetValueOrDefault("OwnerId")) || GameConfiguration.Keys.Any(key => key != "WorldPassword" && !values.ContainsKey(key)))
            throw new IOException("Prepared setup configuration is incomplete.");
        try { _ = GameConfiguration.Merge("", values); }
        catch (ArgumentException error) { throw new IOException("Prepared setup configuration is invalid.", error); }
        if (values.GetValueOrDefault("ServerName") != profile.Name || values.GetValueOrDefault("Port") != profile.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new IOException("Prepared setup configuration does not match its recorded server identity.");
        return values;
    }

    private static IEnumerable<string> CriticalPaths(ServerProfile profile)
    {
        yield return profile.Launcher;
        yield return profile.ConfigPath;
        foreach (var relative in new[] { "RSDragonwilds/Binaries/Win64/RSDragonwildsServer-Win64-Shipping.exe", "RSDragonwilds/Binaries/Linux/RSDragonwildsServer-Linux-Shipping" })
        {
            var path = SafePaths.Resolve(profile.InstallPath, relative);
            if (File.Exists(path)) yield return path;
        }
    }

    private static async Task<(List<BackupFile> Files, List<string> Directories)> InventoryAsync(ServerProfile profile, CancellationToken token)
    {
        SafePaths.NoLinks(profile.InstallPath); SafePaths.NoLinks(profile.SavedPath);
        if (!File.Exists(profile.Launcher) || !File.Exists(profile.ConfigPath) || !Directory.Exists(profile.SavedPath)) throw new IOException("Prepared setup is missing its launcher or configured Saved tree.");
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var directories = new List<string>(); var pending = new Stack<string>(); pending.Push(profile.SavedPath); var count = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested(); var directory = pending.Pop(); SafePaths.NoLinks(directory);
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > MaximumInventoryEntries) throw new IOException("Prepared Saved inventory exceeds the file/directory limit.");
                SafePaths.NoLinks(child);
                if (Directory.Exists(child)) { directories.Add(Relative(profile, child)); pending.Push(child); }
                else if (File.Exists(child)) paths.Add(child);
                else throw new IOException("Prepared Saved inventory contains an unsupported filesystem entry.");
            }
        }
        foreach (var critical in CriticalPaths(profile)) { SafePaths.NoLinks(critical); paths.Add(critical); }
        if (paths.Count > MaximumInventoryEntries) throw new IOException("Prepared setup inventory exceeds the file limit.");
        var files = new List<BackupFile>(); long totalBytes = 0;
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested(); var before = new FileInfo(path); var length = before.Length; var written = before.LastWriteTimeUtc;
            if (length > MaximumFileBytes || (totalBytes += length) > MaximumTotalBytes) throw new IOException("Prepared setup inventory exceeds the verification byte limit.");
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920]; var remaining = length;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                if (read == 0) throw new IOException("Prepared setup file was truncated during verification.");
                hash.AppendData(buffer, 0, read); remaining -= read;
            }
            if (await input.ReadAsync(buffer.AsMemory(0, 1), token) != 0) throw new IOException("Prepared setup file grew during verification.");
            var sha = Convert.ToHexString(hash.GetHashAndReset()); before.Refresh();
            if (!before.Exists || before.Length != length || before.LastWriteTimeUtc != written || input.Length != length) throw new IOException("Prepared setup changed while verifying its files.");
            files.Add(new(Relative(profile, path), length, sha, written));
        }
        return (files, directories);
    }

    private static void ValidateSourceSnapshot(ServerProfile profile, WorldImportPlan? source, List<BackupFile> inventory)
    {
        if (source is null) return;
        if (string.IsNullOrWhiteSpace(source.SourcePath) || !Path.IsPathFullyQualified(source.SourcePath) || string.IsNullOrWhiteSpace(source.FileName) ||
            Path.GetFileName(source.SourcePath) != source.FileName || source.Length is < 32 or > WorldImport.MaximumFileSize || !ValidHash(source.Sha256) || !ValidHash(source.ReviewToken))
            throw new IOException("Prepared world snapshot identity is invalid.");
        var world = SafePaths.Resolve(Path.Combine(profile.SavedPath, "SaveGames"), source.FileName);
        var matching = inventory.Where(file => SafePaths.Same(SafePaths.Resolve(profile.InstallPath, file.Entry), world)).ToArray();
        if (matching.Length != 1 || matching[0].Length != source.Length || matching[0].Sha256 != source.Sha256)
            throw new IOException("Prepared world snapshot does not match its reviewed source copy.");
    }

    private static Dictionary<string, Ownership> ReadRegistry(JsonStore owner)
    {
        var path = Path.Combine(owner.DirectoryPath, RegistryFileName); SafePaths.NoLinks(path);
        if (File.Exists(path) && new FileInfo(path).Length > MaximumDocumentBytes) throw new IOException("Prepared setup ownership registry is oversized.");
        SafePaths.NoLinks(path + ".bak");
        if (File.Exists(path + ".bak") && new FileInfo(path + ".bak").Length > MaximumDocumentBytes) throw new IOException("Prepared setup ownership registry fallback is oversized.");
        Dictionary<string, Ownership>? registry;
        try { registry = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Ownership>>(ReadDocument(path), JsonStore.Options) : new(); }
        catch (JsonException error) { throw new IOException("Prepared setup ownership registry is unreadable.", error); }
        if (registry is null || registry.Count > MaximumPendingSetups || registry.Any(pair => pair.Value is null || pair.Key != pair.Value.ReceiptId ||
            !Guid.TryParseExact(pair.Key, "N", out _) || !Path.IsPathFullyQualified(pair.Value.InstallPath) || !ValidHash(pair.Value.ReceiptToken)))
            throw new IOException("Prepared setup ownership registry is invalid.");
        return registry;
    }

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static byte[] ReadDocument(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumDocumentBytes) throw new IOException("Prepared setup document is empty or oversized.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Prepared setup document changed while reading.");
        return bytes;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Relative(ServerProfile profile, string path) => Path.GetRelativePath(profile.InstallPath, path).Replace('\\', '/');
    private static string NormalizeEntry(string entry) => OperatingSystem.IsWindows() ? entry.ToUpperInvariant() : entry;
    private static IEnumerable<string> Signatures(List<BackupFile> files) => files.Select(file => NormalizeEntry(file.Entry) + "|" + file.Length + "|" + file.Sha256.ToUpperInvariant()).Order(StringComparer.Ordinal);
}
