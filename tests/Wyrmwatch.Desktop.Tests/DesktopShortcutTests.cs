using System.Reflection;
using System.Runtime.InteropServices;
using Wyrmwatch.Desktop;

namespace Wyrmwatch.Desktop.Tests;

public class DesktopShortcutTests
{
    [Fact]
    public void UpdateRetargetsOnlyApplicationShortcutAndPreservesArguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Path.Combine(root, "old.exe"); var current = Path.Combine(root, "new.exe");
        var path = Path.Combine(root, "Wyrmwatch.lnk");
        const string arguments = "--data-dir \"dummy workspace\"";
        try
        {
            Shortcut(path, previous, arguments);
            var original = File.ReadAllBytes(path);
            DesktopIntegration.RetargetShortcut(path, previous, current);
            Assert.Equal((current, arguments), Shortcut(path));
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
            var custom = Path.Combine(root, "custom.lnk"); Shortcut(custom, Path.Combine(root, "unrelated.exe"), arguments);
            var before = File.ReadAllBytes(custom);
            DesktopIntegration.RetargetShortcut(custom, previous, current);
            Assert.Equal(before, File.ReadAllBytes(custom));
            Assert.Empty(Directory.GetFiles(root, "Wyrmwatch-update-*"));
        }
        finally { Directory.Delete(root, true); }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string Target, string Arguments) Shortcut(string path, string? target = null, string? arguments = null)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? link = null;
        try
        {
            link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path])!;
            var type = link.GetType();
            if (target is not null)
            {
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, [target]);
                type.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, [arguments]);
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            return ((string)type.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null)!, (string)type.InvokeMember("Arguments", BindingFlags.GetProperty, null, link, null)!);
        }
        finally { if (link is not null) Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
    }
}
