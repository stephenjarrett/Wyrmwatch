using System.Text.Json;

namespace Wyrmwatch.Core;

public static class SafePaths
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    public static bool Same(string a, string b) => string.Equals(Full(a), Full(b), Comparison);
    public static bool Within(string child, string parent) => Full(child).StartsWith(Full(parent) + Path.DirectorySeparatorChar, Comparison);
    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Linked folders/files are not supported: {current}");
    }
    public static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is ".." or "."))
            throw new IOException("The archive contains an unsafe file path.");
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(result, root)) throw new IOException("The file is outside the selected folder.");
        NoLinks(result);
        return result;
    }
}

public sealed class JsonStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public T Read<T>(string name, Func<T> fallback)
    {
        var path = Path.Combine(DirectoryPath, name);
        var existed = File.Exists(path);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<T>(stream, Options) ?? throw new JsonException("Empty document");
            }
            // Windows replacement can briefly make the target unavailable to a new reader.
            catch (FileNotFoundException)
            {
                if (attempt >= 4) { if (existed) throw new IOException($"Cannot read {name} during replacement. Try again; no preferences were changed."); return fallback(); }
                Thread.Sleep(5);
            }
            catch (DirectoryNotFoundException) { return fallback(); }
            catch (IOException error) when (AtomicFile.SharingViolation(error) && attempt < 20) { Thread.Sleep(10); }
            catch (JsonException e) { throw new IOException($"Cannot read {name}. The original file has been preserved.", e); }
        }
    }
    public void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        AtomicFile.Write(Path.Combine(DirectoryPath, name), JsonSerializer.Serialize(value, Options));
    }
}

public static class AtomicFile
{
    internal static bool SharingViolation(IOException error) => OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 32 or 33;
    public static void Write(string path, string text)
    {
        SafePaths.NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false), leaveOpen: true);
                writer.Write(text); writer.Flush(); file.Flush(true);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
                    else File.Move(temp, path);
                    break;
                }
                catch (IOException error) when (SharingViolation(error) && attempt < 20) { Thread.Sleep(10); }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class OperationHistory(JsonStore store)
{
    private readonly object sync = new();
    private readonly List<OperationRecord> records = store.Read("operations.json", () => new List<OperationRecord>())
        .Select(r => r.Status == "Running" ? r with { Status = "Interrupted", Detail = "The manager closed before this operation finished. Check server state before retrying.", Finished = DateTimeOffset.Now } : r).ToList();
    public IReadOnlyList<OperationRecord> Snapshot() { lock (sync) return records.ToArray(); }
    public void Save(OperationRecord record)
    {
        lock (sync)
        {
            records.RemoveAll(r => r.Id == record.Id); records.Insert(0, record);
            if (records.Count > 500) records.RemoveRange(500, records.Count - 500);
            store.Write("operations.json", records);
        }
    }
}

public static class InstallationLease
{
    public static FileStream Acquire(string installation) => Acquire(installation, Path.GetTempPath());

    internal static FileStream Acquire(string installation, string temporaryRoot)
    {
        var normalized = SafePaths.Full(installation);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
        // Unix temp is shared by accounts. A single owner-writable directory would
        // let the first account block every other account's unrelated installations.
        var directory = Path.Combine(temporaryRoot, OperatingSystem.IsWindows() ? "Wyrmwatch-operation-locks" : "Wyrmwatch-operation-" + key);
        try
        {
            SafePaths.NoLinks(directory);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = Path.Combine(directory, key + ".lock"); SafePaths.NoLinks(path);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new IOException("Cannot access this installation's operation lock. Use the same account for the desktop and background manager, and check its folder permissions.", error);
        }
        catch (IOException) { throw new IOException("Another Wyrmwatch instance is operating on this installation. Wait for it to finish."); }
    }
}

// A workspace gate alone cannot serialize two managers using the same Saved tree.
// Both canonical resources remain locked for the complete operation.
public static class ServerOperationLease
{
    public static IDisposable Acquire(ServerProfile profile)
    {
        profile.Validate();
        var installation = InstallationLease.Acquire(profile.InstallPath);
        try
        {
            var saved = SafePaths.Same(profile.InstallPath, profile.SavedPath) ? null : InstallationLease.Acquire(profile.SavedPath);
            return new Resources(installation, saved);
        }
        catch { installation.Dispose(); throw; }
    }

    private sealed class Resources(FileStream installation, FileStream? saved) : IDisposable
    {
        public void Dispose() { saved?.Dispose(); installation.Dispose(); }
    }
}
