using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Wyrmwatch.Core;

// A read-only preview. Constructing or validating it never creates a directory.
public sealed class ServerCreationPlan
{
    public ServerProfile Profile { get; }
    public IReadOnlyDictionary<string, string> Configuration { get; }

    public ServerCreationPlan(string name, string world, string owner, string adminPassword, string worldPassword,
        int port, string parentFolder, string folderName, string backupParent)
    {
        if (!Path.IsPathFullyQualified(parentFolder) || !Path.IsPathFullyQualified(backupParent))
            throw new ArgumentException("Choose a full path for both parent folders.");
        if (string.IsNullOrWhiteSpace(folderName) || folderName is "." or ".." || folderName.EndsWith('.') || folderName.EndsWith(' ')
            || folderName.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0 || folderName.Any(char.IsControl) || IsReserved(folderName))
            throw new ArgumentException("Use a single folder name without slashes, special characters, or a reserved device name.");
        Profile = new ServerProfile
        {
            Name = name.Trim(), Port = port,
            InstallPath = Path.GetFullPath(Path.Combine(parentFolder, folderName)),
            BackupPath = Path.GetFullPath(Path.Combine(backupParent, folderName))
        };
        Configuration = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            ["ServerName"] = name.Trim(), ["DefaultWorldName"] = world.Trim(), ["OwnerId"] = owner.Trim(),
            ["AdminPassword"] = adminPassword, ["WorldPassword"] = worldPassword, ["Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    public void Validate(IEnumerable<ServerProfile> connections)
    {
        if (string.IsNullOrWhiteSpace(Configuration["OwnerId"]))
            throw new ArgumentException("Paste your Dragonwilds Player ID from the bottom of the in-game Settings menu.");
        _ = GameConfiguration.Merge("", Configuration);
        ServerConnections.Validate(Profile, connections);
        if (connections.Any(p => string.Equals(p.Name, Profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A server with this name is already saved. Choose a different name for the new server.");
        foreach (var path in new[] { Profile.InstallPath, Profile.BackupPath })
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                if (File.Exists(current)) throw new IOException("A file occupies this folder path: " + current);
        if (Directory.Exists(Profile.InstallPath) && Directory.EnumerateFileSystemEntries(Profile.InstallPath).Any())
            throw new IOException("This server folder already contains files:\n" + Profile.InstallPath
                + "\n\nChoose a different server folder name. Create and Import a world always use a new installation. No files were changed.");
        if (Directory.Exists(Profile.InstallPath)) throw new IOException("This folder already exists. Choose a new child folder name so setup can finish without replacing anything.");
    }

    public static string SuggestFolderName(string name)
    {
        var folder = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
        if (folder.Length > 48) folder = folder[..48].TrimEnd('-');
        if (folder.Length == 0) folder = "dragonwilds-server";
        return IsReserved(folder) ? "server-" + folder : folder;
    }

    public static string GeneratePassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    private static bool IsReserved(string name) => Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
