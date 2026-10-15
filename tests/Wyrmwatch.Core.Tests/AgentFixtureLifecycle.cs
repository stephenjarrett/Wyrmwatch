using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

// Test infrastructure only. Never swallow a behavioral assertion or delete an
// unowned workspace to make integration checks pass.
internal static class AgentFixtureLifecycle
{
    internal static async Task RunAsync(Func<Task> body, Func<ValueTask> cleanup, Action<string, Exception>? report = null)
    {
        Exception? primary = null;
        try { await body(); }
        catch (Exception error) { primary = error; Report(report, "primary", error); }
        try { await cleanup(); }
        catch (Exception cleanupError)
        {
            Report(report, "cleanup", cleanupError);
            if (primary is not null) throw new AggregateException("The test and its owned fixture cleanup both failed.", primary, cleanupError);
            throw;
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    internal static string LeaseState(string root)
    {
        var path = Path.Combine(root, "agent.lock");
        try
        {
            if (!File.Exists(path)) return "missing";
            SafePaths.NoLinks(path);
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return "available";
        }
        catch (IOException error) { return "io:" + (error.HResult & 0xffff); }
        catch (UnauthorizedAccessException) { return "unavailable"; }
    }

    internal static string FailureRecord(string stage, string phase, Exception error, string root, IEnumerable<(int Id, string? Token)> spawned)
    {
        string State(int id, string? token)
        {
            if (token is null) return "identity-unavailable";
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.HasExited) return "exited";
                return ProcessLifetime.Token(process) == token ? "alive" : "identity-changed";
            }
            catch (ArgumentException) { return "exited"; }
            catch (Exception failure) when (failure is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            { return "unavailable:" + failure.GetType().Name; }
        }
        // Deliberately exclude exception messages, endpoint files, process
        // arguments, configuration and stream contents, which may hold secrets.
        return JsonSerializer.Serialize(new { Fixture = "agent-smoke", Stage = stage, Phase = phase,
            Error = error.GetType().Name, error.HResult, Lease = LeaseState(root),
            Processes = spawned.Select(p => new { p.Id, StartToken = p.Token, State = State(p.Id, p.Token) }).ToArray() });
    }

    internal static async ValueTask DeleteOwnedRootAsync(string root, TimeSpan? timeout = null, Action<string, Exception>? report = null)
    {
        root = Path.GetFullPath(root);
        if (!SafePaths.Within(root, Path.GetTempPath()) ||
            !Regex.IsMatch(Path.GetFileName(root), "^wyrmwatch-agent-test-[0-9a-f]{32}$"))
            throw new IOException("Refusing cleanup outside an owned agent fixture.");
        SafePaths.NoLinks(root);
        var deadline = timeout ?? TimeSpan.FromSeconds(2);
        if (deadline < TimeSpan.Zero || deadline > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var clock = Stopwatch.StartNew();
        var reported = false;
        while (Directory.Exists(root))
        {
            try
            {
                // Probe before recursive deletion, retaining all evidence if a
                // still-held Windows lease prevents cleanup. This handle must
                // be closed before deleting the directory.
                var lease = Path.Combine(root, "agent.lock");
                if (OperatingSystem.IsWindows() && File.Exists(lease))
                {
                    SafePaths.NoLinks(lease);
                    using var probe = new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                Directory.Delete(root, true);
                return;
            }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 32 or 33)
            {
                if (!reported) { Report(report, "cleanup-sharing", error); reported = true; }
                if (clock.Elapsed >= deadline) throw;
                await Task.Delay(25);
            }
        }
    }

    private static void Report(Action<string, Exception>? report, string phase, Exception error)
    {
        // Diagnostics must never replace the original test or cleanup failure.
        try { report?.Invoke(phase, error); } catch (Exception) { }
    }
}
