using Wyrmwatch.Core;
using Microsoft.Win32;

namespace Wyrmwatch.Desktop;

internal static class DesktopIntegration
{
    // Only retarget the existing application shortcut. Custom shortcuts and their
    // workspace arguments are preserved, and a failed save leaves the old link.
    internal static void RetargetShortcut(string path, string previous, string current)
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
            if (!SafePaths.Same(target, previous) && !SafePaths.Same(target, installed)) return;
            type.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, [current]);
            type.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link, [Path.GetDirectoryName(current)!]);
            type.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, link, [current + ",0"]);
            type.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
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
