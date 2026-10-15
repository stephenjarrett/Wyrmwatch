using System.ComponentModel;
using System.Diagnostics;
using Wyrmwatch.Core;

namespace Wyrmwatch.Platform;

public static class InstallationProcesses
{
    public static void RefuseLegacyOwnershipHelper(string installation)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(installation)) return;
        SafePaths.NoLinks(installation);
        var root = WindowsPhysicalPath.ExistingDirectory(installation);
        foreach (var process in Process.GetProcessesByName("Wyrmwatch.Signal"))
        {
            using (process)
            {
                string? path;
                try
                {
                    if (process.HasExited) continue;
                    path = process.MainModule?.FileName;
                    if (path is null) throw new IOException("Helper executable identity is unavailable.");
                    var parent = Path.GetDirectoryName(path)!;
                    if (Directory.Exists(parent)) path = Path.Combine(WindowsPhysicalPath.ExistingDirectory(parent), Path.GetFileName(path));
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
                {
                    try { if (process.HasExited) continue; } catch (Exception exitError) when (exitError is Win32Exception or InvalidOperationException) { }
                    var unavailable = new IOException("Could not verify running ownership helpers. Nothing was replaced or forcibly closed.");
                    unavailable.Data["InstallerCondition"] = "InspectionUnavailable";
                    throw unavailable;
                }
                if (!SafePaths.Same(path, Path.Combine(root, "Wyrmwatch.Signal.exe"))) continue;
                var legacy = new IOException("An older ownership helper is still using this installation. Complete this one-time migration after you explicitly stop its server when safe. No process was closed.");
                legacy.Data["InstallerCondition"] = "LegacyOwnershipHelper";
                throw legacy;
            }
        }
    }
}
