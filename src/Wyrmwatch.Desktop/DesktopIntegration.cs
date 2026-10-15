using Wyrmwatch.Core;
using Microsoft.Win32;

namespace Wyrmwatch.Desktop;

internal static class DesktopIntegration
{
    internal static void UpdateInstalledShortcuts(string root, string? workspace, string? startMenu = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        var current = Path.Combine(root, "Wyrmwatch.exe");
        var legacyRoots = new[] { workspace, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wyrmwatch") }
            .Where(x => x is not null).Select(x => Path.Combine(x!, "app-updates")).ToArray();
        foreach (var path in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Wyrmwatch.lnk"),
            startMenu ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Wyrmwatch", "Wyrmwatch.lnk") })
        {
            if (!File.Exists(path)) continue;
            SafePaths.NoLinks(path);
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut support is unavailable.");
            var shell = Activator.CreateInstance(type)!; object? shortcut = null;
            string target;
            try
            {
                shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path])!;
                target = (string)shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null)!;
            }
            finally { if (shortcut is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            if (!Path.IsPathFullyQualified(target)) continue;
            if (SafePaths.Same(target, current) || (Path.GetFileName(target).Equals("Wyrmwatch.exe", StringComparison.OrdinalIgnoreCase) && legacyRoots.Any(parent => SafePaths.Within(target, parent))))
                RetargetShortcutCore(path, target, current, workspace);
        }
    }
    // Only retarget the existing application shortcut. Custom shortcuts and their
    // workspace arguments are preserved, and a failed save leaves the old link.
    internal static void RetargetShortcut(string path, string previous, string current)
        => RetargetShortcutCore(path, previous, current, null);
    private static void RetargetShortcutCore(string path, string previous, string current, string? workspace)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return;
        SafePaths.NoLinks(path);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut support is unavailable.");
        var shell = Activator.CreateInstance(shellType)!;
        object? link = null;
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "Wyrmwatch-update-" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            File.Copy(path, temp);
            link = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [temp])!;
            var type = link.GetType();
            var target = (string)type.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, link, null)!;
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Wyrmwatch", "Wyrmwatch.exe");
            if (!Path.IsPathFullyQualified(target) || (!SafePaths.Same(target, previous) && !SafePaths.Same(target, installed))) return;
            type.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, [current]);
            type.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link, [Path.GetDirectoryName(current)!]);
            type.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, link, [current + ",0"]);
            if (workspace is not null)
            {
                var arguments = Wyrmwatch.Platform.WindowsCommandLine.ParseArguments((string)type.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, link, null)!);
                var data = arguments.IndexOf("--data-dir");
                if (data < 0) { arguments.Add("--data-dir"); arguments.Add(workspace); }
                else if (data + 1 >= arguments.Count) arguments.Add(workspace);
                else if (Directory.Exists(arguments[data + 1]) && SafePaths.Same(Wyrmwatch.Platform.WindowsPhysicalPath.ExistingDirectory(arguments[data + 1]), workspace)) arguments[data + 1] = workspace;
                type.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, link, [Wyrmwatch.Platform.WindowsCommandLine.JoinArguments(arguments)]);
            }
            type.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
            SafePaths.NoLinks(path + ".bak");
            File.Replace(temp, path, path + ".bak");
        }
        finally
        {
            if (link is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
    public static void SetStartup(bool enabled)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate the app executable.");
        var arguments = $"--minimized --data-dir \"{Program.DataDirectory}\"";
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled) key.SetValue("Wyrmwatch", $"\"{executable}\" {arguments}");
            else key.DeleteValue("Wyrmwatch", false);
        }
        else if (OperatingSystem.IsLinux())
        {
            var root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var path = Path.Combine(root, "autostart", "wyrmwatch.desktop");
            if (enabled)
            {
                static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";
                AtomicFile.Write(path, $"[Desktop Entry]\nType=Application\nName=Wyrmwatch\nExec={Quote(executable)} --minimized --data-dir {Quote(Program.DataDirectory)}\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
            }
            else if (File.Exists(path)) File.Delete(path);
        }
    }
}
