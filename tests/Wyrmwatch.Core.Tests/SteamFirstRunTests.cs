using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class SteamFirstRunTests
{
    private const string BootstrapFailure = "[----] Update complete, launching...\nERROR! Failed to install app '4019830' (Missing configuration)";
    private static readonly string[] InstallArgs = ["+force_install_dir", "fixture-install", "+login", "anonymous", "+app_update", "4019830", "validate", "+quit"];

    [Fact]
    public async Task FreshBootstrapFailureRefreshesMetadataOnceAndPreservesInstallationArguments()
    {
        var calls = new List<string[]>(); var timeouts = new List<TimeSpan>(); var messages = new List<string>();
        Task<string> Run(string executable, IEnumerable<string> args, string directory, Action<string> log, TimeSpan timeout, CancellationToken token)
        {
            Assert.Equal("steamcmd", executable); Assert.Equal("fixture-tools", directory);
            calls.Add(args.ToArray()); timeouts.Add(timeout);
            return calls.Count == 1 ? Task.FromException<string>(new CommandExecutionException(executable, 7, BootstrapFailure))
                : Task.FromResult("Success! App '4019830' fully installed.");
        }
        var output = await SteamClient.RunInstallAsync("steamcmd", InstallArgs, "fixture-tools", messages.Add,
            TimeSpan.FromMinutes(45), default, true, Run);
        Assert.Contains("fully installed", output);
        Assert.Equal(2, calls.Count); Assert.Equal(InstallArgs, calls[0]);
        Assert.Equal(new[] { "+force_install_dir", "fixture-install", "+login", "anonymous", "+app_info_update", "1", "+app_update", "4019830", "validate", "+quit" }, calls[1]);
        Assert.True(timeouts[1] > TimeSpan.Zero && timeouts[1] <= timeouts[0]);
        Assert.Single(messages); Assert.Contains("retrying once", messages[0]);
    }

    [Theory]
    [InlineData(false, 7, BootstrapFailure)]
    [InlineData(true, 1, BootstrapFailure)]
    [InlineData(true, 7, "ERROR! Failed to install app '4019830' (Missing configuration)")]
    [InlineData(true, 7, "[----] Update complete, launching...\nERROR! Failed to install app '4019830' (Disk write failure)")]
    [InlineData(true, 7, "[----] Update complete, launching...\nERROR! Failed to install app '123' (Missing configuration)")]
    public async Task UnrecognizedOrExistingClientFailuresAreNotRetried(bool downloaded, int exitCode, string output)
    {
        var calls = 0; var failure = new CommandExecutionException("steamcmd", exitCode, output);
        Task<string> Run(string executable, IEnumerable<string> args, string directory, Action<string> log, TimeSpan timeout, CancellationToken token)
        { calls++; return Task.FromException<string>(failure); }
        var observed = await Assert.ThrowsAsync<CommandExecutionException>(() => SteamClient.RunInstallAsync(
            "steamcmd", InstallArgs, "fixture-tools", _ => { }, TimeSpan.FromMinutes(45), default, downloaded, Run));
        Assert.Same(failure, observed); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RepeatedBootstrapFailureStopsAfterOneRetry()
    {
        var calls = 0;
        Task<string> Run(string executable, IEnumerable<string> args, string directory, Action<string> log, TimeSpan timeout, CancellationToken token)
        { calls++; return Task.FromException<string>(new CommandExecutionException(executable, 7, BootstrapFailure)); }
        await Assert.ThrowsAsync<CommandExecutionException>(() => SteamClient.RunInstallAsync("steamcmd", InstallArgs,
            "fixture-tools", _ => { }, TimeSpan.FromMinutes(45), default, true, Run));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationBetweenAttemptsPreventsRetry()
    {
        using var cancellation = new CancellationTokenSource(); var calls = 0;
        Task<string> Run(string executable, IEnumerable<string> args, string directory, Action<string> log, TimeSpan timeout, CancellationToken token)
        { calls++; cancellation.Cancel(); return Task.FromException<string>(new CommandExecutionException(executable, 7, BootstrapFailure)); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SteamClient.RunInstallAsync("steamcmd", InstallArgs,
            "fixture-tools", _ => { }, TimeSpan.FromMinutes(45), cancellation.Token, true, Run));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExhaustedDeadlinePreventsRetry()
    {
        var calls = 0;
        Task<string> Run(string executable, IEnumerable<string> args, string directory, Action<string> log, TimeSpan timeout, CancellationToken token)
        { calls++; return Task.FromException<string>(new CommandExecutionException(executable, 7, BootstrapFailure)); }
        await Assert.ThrowsAsync<CommandExecutionException>(() => SteamClient.RunInstallAsync("steamcmd", InstallArgs,
            "fixture-tools", _ => { }, TimeSpan.Zero, default, true, Run));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SuccessfulFirstAttemptDoesNotRetry()
    {
        var calls = 0;
        Task<string> Run(string executable, IEnumerable<string> args, string directory, Action<string> log, TimeSpan timeout, CancellationToken token)
        { calls++; return Task.FromResult("Success! App '4019830' fully installed."); }
        await SteamClient.RunInstallAsync("steamcmd", InstallArgs, "fixture-tools", _ => throw new Exception("Unexpected recovery log"),
            TimeSpan.FromMinutes(45), default, true, Run);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CommandRunnerRetainsFailureExitCodeAndOutput()
    {
        var windows = OperatingSystem.IsWindows();
        var executable = windows ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : "/bin/sh";
        string[] args = windows ? ["/d", "/c", "echo fixture-output & exit /b 7"] : ["-c", "echo fixture-output; exit 7"];
        var error = await Assert.ThrowsAsync<CommandExecutionException>(() => CommandRunner.RunAsync(executable, args,
            Path.GetTempPath(), _ => { }, TimeSpan.FromSeconds(10)));
        Assert.Equal(7, error.ExitCode); Assert.Contains("fixture-output", error.Output);
    }
}
