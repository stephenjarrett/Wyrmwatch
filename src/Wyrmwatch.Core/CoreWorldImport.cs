using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Wyrmwatch.Core;

public sealed record WorldImportPlan(string SourcePath, string FileName, long Length,
    DateTime LastWriteUtc, string Sha256, string ReviewToken);

/// <summary>
/// Copies one reviewed world into a new, empty staging SaveGames directory. The
/// SAVE/SPUD container checks do not validate game versions or world semantics.
/// No character saves, backups, or other neighbouring files are imported.
/// </summary>
public static class WorldImport
{
    public const long MaximumFileSize = 1024L * 1024 * 1024;
    private const int MaximumChunks = 100_000;

    public static async Task<WorldImportPlan> InspectAsync(string source, CancellationToken token = default)
    {
        var path = ValidateSource(source);
        await using var input = OpenSource(path);
        return await InspectHeldAsync(path, input, token);
    }

    public static Task<string> CopyAsync(WorldImportPlan plan, string destinationSaveGames, CancellationToken token = default) =>
        CopyAsync(plan, destinationSaveGames, null, token);

    internal static async Task<string> CopyAsync(WorldImportPlan plan, string destinationSaveGames,
        Action<string>? checkpoint, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Path.IsPathFullyQualified(destinationSaveGames)) throw new ArgumentException("Choose a full path for the new world's SaveGames folder.");
        var destination = SafePaths.Full(destinationSaveGames); SafePaths.NoLinks(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("World import requires an empty staging SaveGames folder. Existing files were preserved.");
        var source = ValidateSource(plan.SourcePath);
        var sourceParent = Path.GetDirectoryName(source)!;
        if (SafePaths.Same(destination, sourceParent) || SafePaths.Within(source, destination))
            throw new IOException("Choose a separate, empty import destination. The source world and existing files must remain unchanged.");
        var target = SafePaths.Resolve(destination, Path.GetFileName(source));
        var partial = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        var created = false;
        try
        {
            await using (var input = OpenSource(source))
            {
                var reviewed = await InspectHeldAsync(source, input, token);
                RequireSameReview(plan, reviewed);
                token.ThrowIfCancellationRequested();
                SafePaths.NoLinks(destination); Directory.CreateDirectory(destination);
                if (Directory.EnumerateFileSystemEntries(destination).Any()) throw new IOException("The import folder changed during preparation. Existing files were preserved.");
                input.Position = 0;
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    created = true;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920]; long length = 0; int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        length += read;
                        if (length > plan.Length) throw new IOException("The selected world changed while copying. Review it again.");
                        hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    if (length != plan.Length || Convert.ToHexString(hash.GetHashAndReset()) != plan.Sha256)
                        throw new IOException("The selected world changed while copying. Review it again.");
                    await output.FlushAsync(token); output.Flush(true);
                }
            }
            checkpoint?.Invoke("Copied");
            // Reopen the path to detect replacement as well as edits to the held file.
            // Keep the source read handle through publication (denies writes/deletes on Windows).
            _ = ValidateSource(source);
            await using var unchanged = OpenSource(source);
            RequireSameReview(plan, await InspectHeldAsync(source, unchanged, token));
            token.ThrowIfCancellationRequested(); SafePaths.NoLinks(target); SafePaths.NoLinks(partial);
            if (Directory.EnumerateFileSystemEntries(destination).Any(path => !SafePaths.Same(path, partial)))
                throw new IOException("The import folder changed while copying. Existing files were preserved.");
            await using (var copied = File.OpenRead(partial))
                if (Convert.ToHexString(await SHA256.HashDataAsync(copied, token)) != plan.Sha256)
                    throw new IOException("Copied world integrity check failed. No world was published.");
            File.SetLastWriteTimeUtc(partial, plan.LastWriteUtc);
            File.Move(partial, target); created = false;
            return target;
        }
        finally
        {
            if (created)
            {
                try { SafePaths.NoLinks(partial); File.Delete(partial); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Preserve failed staging data if cleanup is unavailable. */ }
            }
        }
    }

    private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);

    private static string ValidateSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source)) throw new ArgumentException("Choose the full path to a world .sav file.");
        var path = Path.GetFullPath(source); SafePaths.NoLinks(path);
        if (!string.Equals(Path.GetExtension(path), ".sav", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a world .sav file, not a character save or backup.");
        var name = Path.GetFileName(path); var stem = name.Split('.')[0].ToUpperInvariant();
        if (name.Any(char.IsControl) || name.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0 || name.EndsWith('.') || name.EndsWith(' ') ||
            stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'))
            throw new IOException("The world filename cannot be copied safely between Windows and Linux. Use a normal .sav filename without reserved names or special characters.");
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Choose an existing regular world .sav file. Linked files are not supported.");
        if (info.Length is < 32 or > MaximumFileSize) throw new IOException("The world file is empty, truncated, or exceeds the 1 GiB import limit.");
        return path;
    }

    private static async Task<WorldImportPlan> InspectHeldAsync(string path, FileStream input, CancellationToken token)
    {
        var before = new FileInfo(path); var length = before.Length; var written = before.LastWriteTimeUtc;
        if (input.Length != length || length is < 32 or > MaximumFileSize) throw new IOException("The selected world changed during inspection. Review it again.");
        ValidateContainer(input, token);
        input.Position = 0;
        var sha = await HashBoundedAsync(input, length, token);
        before.Refresh(); SafePaths.NoLinks(path);
        if (!before.Exists || before.Length != length || before.LastWriteTimeUtc != written || input.Length != length)
            throw new IOException("The selected world changed during inspection. Close the game or source server and review it again.");
        var normalized = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var review = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            normalized + "\n" + length.ToString(CultureInfo.InvariantCulture) + "\n" + written.Ticks.ToString(CultureInfo.InvariantCulture) + "\n" + sha)));
        return new(path, Path.GetFileName(path), length, written, sha, review);
    }

    private static async Task<string> HashBoundedAsync(FileStream input, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; var remaining = length;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
            if (read == 0) throw new IOException("The selected world was truncated during inspection. Review it again.");
            hash.AppendData(buffer, 0, read); remaining -= read;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), token) != 0) throw new IOException("The selected world grew during inspection. Review it again.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void RequireSameReview(WorldImportPlan expected, WorldImportPlan actual)
    {
        if (!SafePaths.Same(expected.SourcePath, actual.SourcePath) || expected.FileName != actual.FileName ||
            expected.Length != actual.Length || expected.LastWriteUtc != actual.LastWriteUtc || expected.Sha256 != actual.Sha256 || expected.ReviewToken != actual.ReviewToken)
            throw new IOException("The selected world differs from the reviewed file. Review it again before importing.");
    }

    private static void ValidateContainer(FileStream input, CancellationToken token)
    {
        // SPUD's author specifies chunk lengths exclude each 8-byte header:
        // https://github.com/sinbad/SPUD/blob/master/Source/SPUD/Public/SpudData.h
        // Dragonwilds SAVE/INFO/GLOB framing is documented by its world-parser author:
        // https://github.com/haithemobeidi/RSDragonWilds-WorldEditor/blob/main/SAVE_FORMAT.md
        input.Position = 0;
        var length = input.Length;
        using var reader = new BinaryReader(input, Encoding.ASCII, true);
        try
        {
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "SAVE")
                throw new IOException("This is not a supported Dragonwilds world SAVE container. Character and input-settings files cannot be imported as worlds.");
            if (reader.ReadUInt32() != length - 8) throw new IOException("World container length is invalid or truncated.");
            var count = 0; var hasGlobal = false;
            while (input.Position < length)
            {
                token.ThrowIfCancellationRequested();
                if (++count > MaximumChunks || length - input.Position < 8) throw new IOException("World container chunk headers are incomplete or exceed the import limit.");
                var magic = reader.ReadBytes(4);
                if (magic.Any(b => b is < 32 or > 126)) throw new IOException("World container contains an invalid chunk identifier.");
                var kind = Encoding.ASCII.GetString(magic); var size = reader.ReadUInt32();
                if (size > length - input.Position) throw new IOException("World container contains a truncated chunk.");
                if (count == 1 && (kind != "INFO" || size < 4)) throw new IOException("World container must begin with nonempty INFO metadata.");
                if (kind == "GLOB") { if (size < 4) throw new IOException("World global data is empty or truncated."); hasGlobal = true; }
                input.Seek(size, SeekOrigin.Current);
            }
            if (!hasGlobal) throw new IOException("World container has no global world data. Choose the world's .sav file.");
        }
        catch (EndOfStreamException error) { throw new IOException("World container is truncated.", error); }
    }
}
