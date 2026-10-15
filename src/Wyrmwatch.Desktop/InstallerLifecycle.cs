using System.Diagnostics;
using System.Text.Json;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

internal sealed record InstallerContext(int ParentId, string ParentToken, string? Workspace, string? StartMenu);
internal sealed record InstallerReceipt(string Status, string Version, int ProcessId, string StartToken, bool ShortcutsUpdated = true);

internal static class InstallerLifecycle
{
    internal static bool TryRun(string[] args, out int result)
    {
        result = 0;
        if (!args.Any(x => x.StartsWith("--installer-", StringComparison.Ordinal))) return false;
        string Value(string option) { var i = Array.IndexOf(args, option); return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new IOException("Missing installer option."); }
        string? Optional(string option) => args.Contains(option) ? Value(option) : null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new IOException("Windows installer helper cannot run on this platform.");
            if (args.Contains("--installer-wait"))
            {
                Wait(int.Parse(Value("--parent")), Value("--parent-token")); return true;
            }
            if (args.Contains("--installer-prepare"))
            {
                var root = Path.GetFullPath(Value("--root"));
                var workspace = Optional("--workspace");
                var effectiveWorkspace = workspace ?? Program.DataDirectory;
                if (Inside(root, effectiveWorkspace) || Inside(effectiveWorkspace, root))
                    throw new IOException("The application and server workspace must be separate.");
                if (File.Exists(Path.Combine(root, "Wyrmwatch.exe")) && !File.Exists(Path.Combine(root, "Wyrmwatch.runtimeconfig.json")))
                    throw new IOException("The selected installation contains an unrecognized application.");
                if (Directory.Exists(root) && !File.Exists(Path.Combine(root, "Wyrmwatch.exe")) && Directory.EnumerateFileSystemEntries(root).Any())
                    throw new IOException("Choose an empty dedicated installation directory.");
                var parentId = int.Parse(Value("--parent")); using var parent = Process.GetProcessById(parentId);
                var journal = Path.Combine(Path.GetTempPath(), "wyrmwatch-install-" + Guid.NewGuid().ToString("N"));
                AppInstallationTransaction.Prepare(root, AppContext.BaseDirectory, journal, typeof(Program).Assembly.GetName().Version!.ToString(3));
                AppInstallationTransaction.CopySupport(AppContext.BaseDirectory, journal);
                File.WriteAllBytes(Path.Combine(journal, "context.json"), JsonSerializer.SerializeToUtf8Bytes(new InstallerContext(parentId, ProcessLifetime.Token(parent), workspace, Optional("--start-menu"))));
                // Inno reads this non-secret reference before it deletes its own temp folder.
                File.WriteAllText(Value("--reference"), journal, new System.Text.UTF8Encoding(false));
                return true;
            }
            var location = Path.GetFullPath(Value("--journal"));
            var snapshot = AppInstallationTransaction.ReadSnapshot(location);
            if (args.Contains("--installer-verify")) { AppInstallationTransaction.VerifyInstalled(location); return true; }
            if (!args.Contains("--installer-finish")) throw new IOException("Unknown installer action.");
            var contextPath = Path.Combine(location, "context.json"); SafePaths.NoLinks(contextPath);
            var context = JsonSerializer.Deserialize<InstallerContext>(File.ReadAllBytes(contextPath)) ?? throw new IOException("Installer context is missing.");
            Wait(context.ParentId, context.ParentToken);
            var success = args.Contains("--success");
            if (success) { try { AppInstallationTransaction.VerifyInstalled(location); } catch (IOException) { success = false; } }
            if (!success) AppInstallationTransaction.Restore(location);
            var shortcutsUpdated = true;
            if (success)
            {
                try { DesktopIntegration.UpdateInstalledShortcuts(snapshot.Root, context.Workspace, context.StartMenu); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or System.Reflection.TargetInvocationException) { shortcutsUpdated = false; }
            }
            using var current = Process.GetCurrentProcess();
            var receipt = new InstallerReceipt(success ? "installed" : "restored", snapshot.Version, current.Id, ProcessLifetime.Token(current), shortcutsUpdated);
            File.WriteAllBytes(Path.Combine(location, "completed.json"), JsonSerializer.SerializeToUtf8Bytes(receipt));
            if (context.Workspace is not null)
            {
                var start = new ProcessStartInfo(Path.Combine(snapshot.Root, "Wyrmwatch.exe")) { UseShellExecute = false, WorkingDirectory = snapshot.Root };
                foreach (var value in new[] { "--data-dir", context.Workspace, "--apply-startup", "--installation-result", location }) start.ArgumentList.Add(value);
                using var child = Process.Start(start) ?? throw new IOException("Could not reopen the installed application.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Record only code locations and exception categories. Configuration,
            // process arguments, and exception messages may contain user secrets.
            try
            {
                var diagnostic = args.Contains("--installer-prepare") ? Value("--reference") + ".error.json"
                    : args.Contains("--journal") ? Path.Combine(Value("--journal"), "failure.json") : null;
                if (diagnostic is not null)
                    File.WriteAllBytes(diagnostic, JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        Error = error.GetType().Name, error.HResult,
                        Methods = new StackTrace(error).GetFrames().Select(frame => frame.GetMethod()?.Name).ToArray()
                    }));
            }
            catch (Exception diagnosticError) when (diagnosticError is IOException or UnauthorizedAccessException or ArgumentException) { }
            result = 1;
        }
        return true;
    }

    private static void Wait(int id, string token)
    {
        try { using var process = Process.GetProcessById(id); if (ProcessLifetime.Token(process) == token && !process.WaitForExit(30000)) throw new IOException("The previous process did not exit; nothing was forcibly closed."); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
    }
    private static bool Inside(string parent, string child) => SafePaths.Same(parent, child) ||
        Path.GetFullPath(child).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static string? Outcome(string directory, string root)
    {
        try
        {
            var snapshot = AppInstallationTransaction.ReadSnapshot(directory);
            if (!SafePaths.Same(snapshot.Root, root)) return null;
            var path = Path.Combine(directory, "completed.json"); SafePaths.NoLinks(path);
            var receipt = JsonSerializer.Deserialize<InstallerReceipt>(File.ReadAllBytes(path));
            return receipt?.Status == "restored" ? "The update did not complete. Your previous application was restored; your server connections and game data are unchanged."
                : receipt?.ShortcutsUpdated == false ? "The update is installed. Existing shortcuts could not be refreshed; open the installed Wyrmwatch application to review App updates."
                : receipt?.Status == "installed" ? "Wyrmwatch updated and reconnected using your saved server connections." : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    internal static void CleanupCompleted(string root)
    {
        // Only self-created, completed recovery folders belonging to this exact
        // installation are eligible. A live helper or failed recovery is retained.
        foreach (var directory in Directory.EnumerateDirectories(Path.GetTempPath(), "wyrmwatch-install-*"))
        {
            try
            {
                AppInstallationTransaction.ValidateJournalDirectory(directory);
                var completed = Path.Combine(directory, "completed.json");
                if (!File.Exists(completed)) continue;
                var snapshot = AppInstallationTransaction.ReadSnapshot(directory);
                if (!SafePaths.Same(snapshot.Root, root)) continue;
                var receipt = JsonSerializer.Deserialize<InstallerReceipt>(File.ReadAllBytes(completed));
                if (receipt is null || receipt.Status is not ("installed" or "restored")) continue;
                try { using var helper = Process.GetProcessById(receipt.ProcessId); if (ProcessLifetime.Token(helper) == receipt.StartToken && !helper.WaitForExit(10000)) continue; }
                catch (ArgumentException) { }
                // Recheck every entry for links before recursive removal.
                var pending = new Stack<string>(); pending.Push(directory);
                while (pending.TryPop(out var folder))
                    foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                    { SafePaths.NoLinks(entry); if (Directory.Exists(entry)) pending.Push(entry); }
                Directory.Delete(directory, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}
