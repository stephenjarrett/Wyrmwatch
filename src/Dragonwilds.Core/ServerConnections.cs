namespace Dragonwilds.Core;

public static class ServerConnections
{
    private static bool Overlap(string a, string b) => SafePaths.Same(a, b) || SafePaths.Within(a, b) || SafePaths.Within(b, a);

    public static void Validate(ServerProfile profile, IEnumerable<ServerProfile> connections)
    {
        profile.Validate();
        foreach (var other in connections.Where(p => p.Id != profile.Id))
        {
            if (Overlap(profile.InstallPath, other.InstallPath))
                throw new ArgumentException($"The installation overlaps '{other.Name}'. Choose a separate installation folder.");
            if (Overlap(profile.SavedPath, other.SavedPath) || Overlap(profile.SavedPath, other.InstallPath) || Overlap(profile.InstallPath, other.SavedPath))
                throw new ArgumentException($"The save-data folder overlaps '{other.Name}'. Each server needs its own save-data folder.");
            if (Overlap(profile.BackupPath, other.InstallPath) || Overlap(profile.BackupPath, other.SavedPath) || Overlap(other.BackupPath, profile.InstallPath) || Overlap(other.BackupPath, profile.SavedPath))
                throw new ArgumentException($"A backup folder overlaps game files belonging to '{other.Name}'. Store backups separately from all servers.");
            if (profile.Port == other.Port)
                throw new ArgumentException($"Port {profile.Port} is already assigned to '{other.Name}'. Each server needs a distinct game port.");
        }
    }

    public static int AvailablePort(IEnumerable<ServerProfile> connections)
    {
        var used = connections.Select(p => p.Port).ToHashSet();
        return Enumerable.Range(7777, 65535 - 7777 + 1).First(port => !used.Contains(port));
    }
}

public static class ExistingServerImport
{
    // Read only: connecting an installation never copies, installs, or modifies game files.
    public static ServerProfile Inspect(string launcher, string savedPath, string backupPath)
    {
        if (!Path.IsPathFullyQualified(launcher) || !File.Exists(launcher)) throw new ArgumentException("Choose an existing server launcher.");
        SafePaths.NoLinks(launcher);
        var profile = new ServerProfile { InstallPath = Path.GetDirectoryName(launcher)!, LauncherPath = launcher, DataPath = savedPath, BackupPath = backupPath };
        profile.Validate();
        if (!Directory.Exists(profile.SavedPath)) throw new ArgumentException("Choose this server's existing Saved folder. No folders will be created during import.");
        SafePaths.NoLinks(profile.ConfigPath);
        var values = GameConfiguration.Read(profile.ConfigPath);
        if (values.TryGetValue("Port", out var portText))
        {
            if (!int.TryParse(portText, out var port)) throw new ArgumentException("The existing configuration contains an invalid port.");
            profile = profile with { Port = port };
        }
        profile = profile with { Name = values.GetValueOrDefault("ServerName", Path.GetFileName(profile.InstallPath)) };
        profile.Validate();
        return profile;
    }
}
