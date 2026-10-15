using Wyrmwatch.Core;
using Xunit.Abstractions;

namespace Wyrmwatch.Core.Tests;

public class AgentFixtureLifecycleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PrimaryAndCleanupFailuresBothSurviveEvenWhenReportingFails()
    {
        var primary = new InvalidOperationException("primary fixture failure");
        var cleanup = new IOException("cleanup fixture failure");
        var error = await Assert.ThrowsAsync<AggregateException>(() => AgentFixtureLifecycle.RunAsync(
            () => Task.FromException(primary), () => ValueTask.FromException(cleanup), (_, _) => throw new IOException("diagnostic failure")));
        Assert.Collection(error.InnerExceptions, e => Assert.Same(primary, e), e => Assert.Same(cleanup, e));
    }

    [Fact]
    public async Task SuccessfulCleanupCannotTurnAnAssertionFailureIntoSuccess()
    {
        var primary = new InvalidOperationException("preserve this failure");
        var cleaned = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AgentFixtureLifecycle.RunAsync(
            () => Task.FromException(primary), () => { cleaned = true; return ValueTask.CompletedTask; }));
        Assert.Same(primary, error); Assert.True(cleaned);
    }

    [Fact]
    public async Task TemporaryWindowsLeaseRetainsEvidenceUntilReleasedThenCleansUp()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        var sentinel = Path.Combine(root, "world.sav"); File.WriteAllText(sentinel, "untouched synthetic world");
        using var held = WorkspaceLease.Acquire(root);
        try
        {
            Assert.StartsWith("io:", AgentFixtureLifecycle.LeaseState(root));
            var contention = 0;
            var waiting = AgentFixtureLifecycle.DeleteOwnedRootAsync(root, report: (_, _) => contention++).AsTask();
            Assert.Equal(1, contention);
            Assert.False(waiting.IsCompleted);
            Assert.Equal("untouched synthetic world", File.ReadAllText(sentinel));
            held.Dispose();
            await waiting;
            Assert.False(Directory.Exists(root));
        }
        finally { held.Dispose(); if (Directory.Exists(root)) await AgentFixtureLifecycle.DeleteOwnedRootAsync(root); }
    }

    [Fact]
    public async Task ConcurrentOwnedWorkspacesWaitForTheirOwnLeaseWithoutCrossDeletion()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            var root = NewRoot(); var sentinel = Path.Combine(root, "world.sav"); File.WriteAllText(sentinel, index.ToString());
            using var held = WorkspaceLease.Acquire(root);
            try
            {
                var cleanup = AgentFixtureLifecycle.DeleteOwnedRootAsync(root).AsTask();
                Assert.False(cleanup.IsCompleted);
                await Task.Yield();
                Assert.Equal(index.ToString(), File.ReadAllText(sentinel));
                held.Dispose(); await cleanup;
                Assert.False(Directory.Exists(root));
            }
            finally { held.Dispose(); if (Directory.Exists(root)) await AgentFixtureLifecycle.DeleteOwnedRootAsync(root); }
        }));
    }

    [Fact]
    public async Task PersistentLeaseStillFailsAndPreservesPrimaryFailureAndEvidence()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot(); var sentinel = Path.Combine(root, "world.sav"); File.WriteAllText(sentinel, "preserved");
        using var held = WorkspaceLease.Acquire(root);
        try
        {
            var primary = new InvalidOperationException("original behavioral failure");
            var error = await Assert.ThrowsAsync<AggregateException>(() => AgentFixtureLifecycle.RunAsync(
                () => Task.FromException(primary), () => AgentFixtureLifecycle.DeleteOwnedRootAsync(root, TimeSpan.FromMilliseconds(100))));
            Assert.Same(primary, error.InnerExceptions[0]);
            Assert.IsType<IOException>(error.InnerExceptions[1]);
            Assert.Equal("preserved", File.ReadAllText(sentinel));
            Assert.True(File.Exists(Path.Combine(root, "agent.lock")));
        }
        finally { held.Dispose(); await AgentFixtureLifecycle.DeleteOwnedRootAsync(root); }
    }

    [Fact]
    public void FailureRecordContainsCategoriesAndOwnedIdentityWithoutSensitiveMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-agent-test-" + Guid.NewGuid().ToString("N"));
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var record = AgentFixtureLifecycle.FailureRecord("await-exit", "primary", new IOException("sensitive fixture message"), root,
            [(process.Id, ProcessLifetime.Token(process))]);
        Assert.Contains("await-exit", record); Assert.Contains("IOException", record); Assert.Contains("alive", record);
        Assert.DoesNotContain("sensitive fixture message", record);
        Assert.DoesNotContain("Secret", record); Assert.DoesNotContain("Address", record);
        output.WriteLine(record); // The TRX must retain this through xUnit's test output channel.
    }

    [Fact]
    public async Task UnownedDirectoryIsRejectedBeforeMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-unowned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var sentinel = Path.Combine(root, "sentinel"); File.WriteAllText(sentinel, "keep");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => AgentFixtureLifecycle.DeleteOwnedRootAsync(root).AsTask());
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally { File.Delete(sentinel); Directory.Delete(root); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "wyrmwatch-agent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
}
