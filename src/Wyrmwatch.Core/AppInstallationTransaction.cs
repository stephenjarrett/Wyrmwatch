using System.Security.Cryptography;
using System.Text.Json;

namespace Wyrmwatch.Core;

public sealed record InstallationFile(string Name, string? PreviousHash, string? IncomingHash);
public sealed record InstallationSnapshot(string Root, string Version, InstallationFile[] Files);

// The installer can overwrite files before it reports failure. Keep independently
// verified originals outside both the installation and Inno's temporary directory.
// Only paths owned by a published package are included; user data is never rolled back.
public static class AppInstallationTransaction
{
    public const string ManifestName = "app-files.json";
    public const long BackupLimit = 300L * 1024 * 1024;
    public const string JournalName = "installation.json";

    public static Dictionary<string, string> ReadManifest(string path)
    {
        SafePaths.NoLinks(path);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("Application file manifest is too large.");
        var files = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path))
            ?? throw new IOException("Application file manifest is missing.");
        if (files.Count is < 4 or > 4000) throw new IOException("Application file manifest has an invalid size.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hash) in files)
        {
            ValidateName(name);
            if (!names.Add(name) || !ValidHash(hash)) throw new IOException("Application file manifest is invalid.");
        }
        foreach (var required in new[] { "Wyrmwatch.exe", "Wyrmwatch.dll", "Wyrmwatch.runtimeconfig.json", "agent/Wyrmwatch.Agent.exe" })
            if (!files.ContainsKey(required)) throw new IOException("Application file manifest is incomplete.");
        return files;
    }

    public static InstallationSnapshot Prepare(string root, string incomingDirectory, string journalDirectory, string version)
    {
        root = Path.GetFullPath(root); incomingDirectory = Path.GetFullPath(incomingDirectory);
        if (SafePaths.Same(root, Path.GetPathRoot(root)!) || SafePaths.Same(root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ||
            SafePaths.Within(incomingDirectory, root) || SafePaths.Within(root, incomingDirectory) || SafePaths.Same(root, incomingDirectory))
            throw new IOException("Choose a separate dedicated application installation directory.");
        SafePaths.NoLinks(root); SafePaths.NoLinks(incomingDirectory); ValidateJournalDirectory(journalDirectory);
        if (Directory.Exists(journalDirectory)) throw new IOException("Installation recovery directory already exists.");
        var incoming = ReadManifest(Path.Combine(incomingDirectory, ManifestName));
        VerifyFiles(incomingDirectory, incoming);
        incoming.Add(ManifestName, Hash(Path.Combine(incomingDirectory, ManifestName)));
        var names = new HashSet<string>(incoming.Keys, StringComparer.OrdinalIgnoreCase);
        var previousManifest = Path.Combine(root, ManifestName);
        if (File.Exists(previousManifest)) names.UnionWith(ReadManifest(previousManifest).Keys);
        // Exact retired runtime paths are generated and validated by packaging.
        var retiredPath = Path.Combine(incomingDirectory, "retired-package-files.json");
        if (File.Exists(retiredPath))
            foreach (var name in JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(retiredPath)) ?? [])
            {
                ValidateName(name);
                if (!(name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("agent/Wyrmwatch.Agent.", StringComparison.Ordinal))) throw new IOException("Invalid retired application file.");
                names.Add(name);
            }
        // Preserve installer registration files too, so an interrupted legacy
        // migration does not discard the previous uninstaller.
        if (Directory.Exists(root))
            foreach (var file in Directory.EnumerateFiles(root, "unins*", SearchOption.TopDirectoryOnly))
                if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"^unins\d{3}\.(exe|dat|msg)$")) names.Add(Path.GetFileName(file));
        if (names.Count > 5000) throw new IOException("Too many application recovery files.");
        long bytes = 0;
        var entries = new List<InstallationFile>();
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            var path = SafePaths.Resolve(root, name); SafePaths.NoLinks(path);
            if (Directory.Exists(path)) throw new IOException("An application file path is occupied by a directory.");
            if (File.Exists(path)) bytes += new FileInfo(path).Length;
            if (bytes > BackupLimit) throw new IOException("Application recovery backup exceeds its size limit.");
            entries.Add(new(name, File.Exists(path) ? Hash(path) : null, incoming.GetValueOrDefault(name)));
        }
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(journalDirectory))!);
        var supportBytes = incoming.Keys.Sum(name => new FileInfo(SafePaths.Resolve(incomingDirectory, name)).Length);
        if (drive.AvailableFreeSpace < bytes + supportBytes + 64L * 1024 * 1024)
            throw new IOException("There is not enough disk space for a verified recovery backup.");
        Directory.CreateDirectory(journalDirectory);
        foreach (var entry in entries.Where(x => x.PreviousHash is not null))
        {
            var source = SafePaths.Resolve(root, entry.Name);
            var backup = SafePaths.Resolve(Path.Combine(journalDirectory, "original"), entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(source, backup, false);
            if (Hash(backup) != entry.PreviousHash) throw new IOException("Application changed during recovery backup. Installation was stopped.");
        }
        var snapshot = new InstallationSnapshot(root, version, entries.ToArray());
        Write(Path.Combine(journalDirectory, JournalName), snapshot);
        return snapshot;
    }

    public static void CopySupport(string incomingDirectory, string journalDirectory)
    {
        ValidateJournalDirectory(journalDirectory);
        var files = ReadManifest(Path.Combine(incomingDirectory, ManifestName));
        files.Add(ManifestName, Hash(Path.Combine(incomingDirectory, ManifestName)));
        foreach (var (name, hash) in files)
        {
            var destination = SafePaths.Resolve(Path.Combine(journalDirectory, "support"), name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(SafePaths.Resolve(incomingDirectory, name), destination, false);
            if (Hash(destination) != hash) throw new IOException("Recovery helper verification failed.");
        }
    }

    public static InstallationSnapshot ReadSnapshot(string journalDirectory)
    {
        ValidateJournalDirectory(journalDirectory);
        var path = Path.Combine(journalDirectory, JournalName); SafePaths.NoLinks(path);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("Recovery journal is too large.");
        var snapshot = JsonSerializer.Deserialize<InstallationSnapshot>(File.ReadAllBytes(path)) ?? throw new IOException("Recovery journal is missing.");
        if (snapshot.Files is not { Length: > 0 and <= 5000 } || !Path.IsPathFullyQualified(snapshot.Root)) throw new IOException("Recovery journal is invalid.");
        SafePaths.NoLinks(snapshot.Root);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files)
        {
            ValidateName(file.Name);
            if (!names.Add(file.Name) || (file.PreviousHash is not null && !ValidHash(file.PreviousHash)) ||
                (file.IncomingHash is not null && !ValidHash(file.IncomingHash))) throw new IOException("Recovery journal is invalid.");
        }
        return snapshot;
    }

    public static void VerifyInstalled(string journalDirectory)
    {
        var snapshot = ReadSnapshot(journalDirectory);
        VerifyFiles(snapshot.Root, snapshot.Files.Where(x => x.IncomingHash is not null).ToDictionary(x => x.Name, x => x.IncomingHash!));
    }

    public static void Restore(string journalDirectory)
    {
        var snapshot = ReadSnapshot(journalDirectory);
        // Verify every original before changing anything. Corrupt backups are
        // retained for diagnosis and must never be promoted into the install.
        VerifyFiles(Path.Combine(journalDirectory, "original"), snapshot.Files.Where(x => x.PreviousHash is not null).ToDictionary(x => x.Name, x => x.PreviousHash!));
        foreach (var file in snapshot.Files.Where(x => x.PreviousHash is not null))
        {
            var target = SafePaths.Resolve(snapshot.Root, file.Name); SafePaths.NoLinks(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".recovery-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(SafePaths.Resolve(Path.Combine(journalDirectory, "original"), file.Name), temporary, false);
                if (Hash(temporary) != file.PreviousHash) throw new IOException("Recovery copy verification failed. The original backup is retained.");
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        foreach (var file in snapshot.Files.Where(x => x.PreviousHash is null && x.IncomingHash is not null))
        {
            var target = SafePaths.Resolve(snapshot.Root, file.Name); SafePaths.NoLinks(target);
            // Preserve an unexpected/user-created file rather than deleting it.
            if (File.Exists(target) && Hash(target) == file.IncomingHash) File.Delete(target);
        }
        VerifyFiles(snapshot.Root, snapshot.Files.Where(x => x.PreviousHash is not null).ToDictionary(x => x.Name, x => x.PreviousHash!));
    }

    private static void VerifyFiles(string root, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (name, hash) in files)
        {
            var path = SafePaths.Resolve(root, name); SafePaths.NoLinks(path);
            if (!File.Exists(path) || Hash(path) != hash) throw new IOException("Application file verification failed: " + name);
        }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 300 || name.Contains('\\') || name.StartsWith('/') ||
            name.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':')))
            throw new IOException("Unsafe application file path.");
    }
    public static void ValidateJournalDirectory(string directory)
    {
        var full = Path.GetFullPath(directory); var parent = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        if (!string.Equals(Path.GetDirectoryName(full), parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full), @"^wyrmwatch-install-[0-9a-f]{32}$"))
            throw new IOException("Recovery directory is outside the owned temporary location.");
        SafePaths.NoLinks(full);
    }
    private static void Write<T>(string path, T value)
    {
        var temporary = path + ".tmp"; File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value)); File.Move(temporary, path, false);
    }
}
