using Wyrmwatch.Core;
using Microsoft.Win32;

namespace Wyrmwatch.Desktop;

internal static class DesktopIntegration
{
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
