using Wyrmwatch.Core;
using Wyrmwatch.Desktop;

namespace Wyrmwatch.Desktop.Tests;

public class WorkspaceModelTests : IsolatedDesktopTest
{
    [Fact]
    public void AnotherObservedServerBlocksStartWithDestinationAndUnknownStateStaysBlocked()
    {
        var model = Server(); model.ServerRunning = false;
        var other = new ManagedServer("other", "Other running fixture", ServerSnapshot.Offline with { Running = true }, new(), null);
        model.UpdateStartPrerequisites([other]);
        Assert.False(model.CanStart); Assert.Contains(other.Name, model.StartHint);
        Assert.Equal("RunningServer", model.StartHelpDestination); Assert.Contains(other.Name, model.StartHelpText);
        model.UpdateStartPrerequisites([other with { State = ServerSnapshot.Offline with { Accessible = false } }]);
        Assert.False(model.CanStart); Assert.Equal("Refresh", model.StartHelpDestination);
        model.UpdateStartPrerequisites([other with { State = ServerSnapshot.Offline }]);
        Assert.True(model.CanStart);
    }

    [Fact]
    public void SuccessfulSetupClearsOnlyItsOwnPriorFailure()
    {
        var model = new WorkspaceModel();
        model.ReportError("World import stopped", "Fixture failure", owner: "setup:one");
        model.ClearErrorFor("setup:two");
        Assert.True(model.HasError);
        model.ClearErrorFor("setup:one");
        Assert.False(model.HasError);
        model.ReportError("World import stopped", "Fixture failure", owner: "setup:one");
        model.ReportError("Backup verification needs attention", "Unrelated fixture error", "Backups");
        model.ClearErrorFor("setup:one");
        Assert.True(model.HasError); Assert.Equal("Backups", model.ErrorDestination);
        Assert.Equal("Backup verification needs attention", model.ErrorSummary);
    }

    private static readonly DateTimeOffset Clock = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static WorkspaceModel Server() => new() { SelectedProfile = new ServerProfile { Id = "fixture", Name = "Fixture server" } };

    [Fact]
    public void OperationKeepsItsServerContextAndMeasuredTimeUntilCompletion()
    {
        var originalDemo = Program.Demo; Program.Demo = false;
        try
        {
            var model = Server(); model.ServerRunning = false;
            model.BeginOperation("Restore backup", "Fixture server", "Waiting for manager", Clock);
            model.SelectedProfile = new ServerProfile { Id = "another", Name = "Another server" };
            model.UpdateOperationPhase("Processing server request", "Verifying archive hashes", Clock.AddSeconds(9));
            var announcement = model.Announcement;
            model.TickOperation(Clock.AddMinutes(2).AddSeconds(7));
            Assert.Equal("Fixture server", model.OperationServer);
            Assert.Equal("Elapsed 2m 07s", model.OperationElapsed);
            Assert.Equal("Verifying archive hashes", model.OperationLatestActivity);
            Assert.Equal(announcement, model.Announcement);
            Assert.False(model.CanManage);
            // A manager refresh cannot unlock controls while a local request remains in flight.
            model.Busy = false; Assert.True(model.Busy); Assert.False(model.CanStart);
            model.FinishOperation("Archive restored; server remains stopped.", true, Clock.AddMinutes(3));
            Assert.False(model.OperationActive); Assert.False(model.Busy);
            Assert.Contains("Fixture server", model.LastCompletion);
            Assert.Contains("Completed", model.LastCompletion);
            Assert.True(model.HasLastCompletion);
            var elapsed = model.OperationElapsed;
            model.TickOperation(Clock.AddHours(1)); Assert.Equal(elapsed, model.OperationElapsed);
        }
        finally { Program.Demo = originalDemo; }
    }

    [Fact]
    public void FailedRefreshRetainsReadingsButFailsClosedAndOffersRetry()
    {
        var originalDemo = Program.Demo; Program.Demo = false;
        try
        {
            var model = Server(); Assert.True(model.FirstLoading);
            Assert.Contains("first time", model.ServerStateHint);
            model.CompleteRefresh(true, now: Clock); model.ServerRunning = true;
            model.Cpu = "7.2%"; model.Memory = "1.1 GB";
            model.RecordResourceSample(); model.RecordResourceSample(); Assert.False(model.ShowResourceEmpty);
            model.BeginRefresh(); Assert.True(model.StateStale); Assert.False(model.CanStart);
            model.FailRefresh("Manager did not answer");
            Assert.Null(model.ServerRunning); Assert.True(model.InspectionUnknown); Assert.True(model.StateStale);
            Assert.False(model.FirstLoading); Assert.False(model.CanStart); Assert.False(model.CanStop);
            Assert.False(model.CanConfigure); Assert.False(model.CanRestore);
            Assert.Equal("7.2%", model.Cpu); Assert.Equal("1.1 GB", model.Memory);
            Assert.Equal("Refresh", model.StartHelpDestination);
            Assert.Equal("Retry server check", model.StartHelpText);
            Assert.Contains("unavailable", model.ResourcesHint);
            model.BeginRefresh(); model.CompleteRefresh(true, now: Clock.AddMinutes(1)); model.ServerRunning = false;
            Assert.False(model.StateStale); Assert.False(model.InspectionUnknown); Assert.True(model.CanStart);
            Assert.Equal("Stopped", model.StatusChipText);
        }
        finally { Program.Demo = originalDemo; }
    }

    [Fact]
    public void InitialInaccessibleInspectionIsNotPresentedAsLoadingOrStopped()
    {
        var model = Server(); model.CompleteRefresh(false, "Process inspection denied", Clock);
        Assert.False(model.FirstLoading); Assert.True(model.InspectionUnknown); Assert.False(model.StateStale);
        Assert.Null(model.ServerRunning); Assert.False(model.CanStart); Assert.False(model.CanStop);
        Assert.Equal("Inspection unavailable", model.StatusChipText);
        Assert.Contains("inspection", model.ServerStateHint);
        Assert.Equal("Process inspection denied", model.InspectionDetail);
    }

    [Fact]
    public void PrerequisiteDestinationsFollowRecoveryAndLifecycleGuards()
    {
        var originalDemo = Program.Demo; Program.Demo = false;
        try
        {
            var model = Server(); model.ServerRunning = false;
            Assert.True(model.CanRestore); Assert.False(model.CanRestoreSelected);
            Assert.Equal("SelectBackup", model.RestoreHelpDestination);
            model.BackupSelected = true; Assert.True(model.CanRestoreSelected);
            model.ServerRunning = true; Assert.False(model.CanRestoreSelected);
            Assert.Equal("Stop", model.RestoreHelpDestination);
            model.RecoveryPending = true;
            Assert.Equal("Backups", model.StartHelpDestination);
            Assert.Equal("Backups", model.RestoreHelpDestination);
            Assert.Equal("Recover here", model.RestoreHelpText);
            Assert.Equal("Backups", model.UpdateHelpDestination);
            Assert.False(model.CanUpdate); Assert.False(model.CanStart); Assert.False(model.CanRestoreSelected);
            Assert.True(model.CanStop); Assert.False(model.CanRecover);
            model.ServerRunning = false; Assert.True(model.CanRecover);
            model.RecoveryPending = false;
            Assert.Equal("CheckUpdates", model.UpdateHelpDestination); Assert.False(model.CanUpdate);
            model.InstalledBuildKnown = true; Assert.True(model.CanUpdate);
            model.Busy = true; Assert.False(model.CanUpdate); Assert.False(model.CanRestoreSelected); Assert.False(model.CanStart);
            Assert.Equal("Activity", model.StartHelpDestination);
            model.Busy = false; Program.Demo = true;
            Assert.False(model.CanStart); Assert.False(model.CanRestoreSelected); Assert.False(model.CanUpdate); Assert.False(model.CanManage);
        }
        finally { Program.Demo = originalDemo; }
    }

    [Fact]
    public void ResourcesDistinguishStoppedCollectingAndUnavailableWithoutInventingZeroUsage()
    {
        var model = Server(); model.CompleteRefresh(true, now: Clock); model.ServerRunning = false;
        Assert.True(model.ShowResourceEmpty); Assert.Contains("Server stopped", model.ResourcesHint);
        model.RecordResourceSample(); model.ServerRunning = true;
        Assert.Contains("Collecting samples", model.ResourcesHint);
        model.RecordResourceSample(); Assert.True(model.ShowResourceEmpty);
        model.RecordResourceSample(); Assert.False(model.ShowResourceEmpty);
        model.FailRefresh("Inspection denied"); Assert.True(model.ShowResourceEmpty);
        Assert.Contains("unavailable", model.ResourcesHint);
        model.SelectedProfile = new ServerProfile { Id = "new", Name = "New server" };
        Assert.True(model.FirstLoading); Assert.False(model.InspectionUnknown); Assert.False(model.BackupSelected);
        model.CompleteRefresh(true, now: Clock); model.ServerRunning = true;
        Assert.True(model.ShowResourceEmpty); Assert.Contains("Collecting samples", model.ResourcesHint);
    }

    [Fact]
    public void ErrorRemainsActionableThroughPollingAndLaterOperationsUntilDismissed()
    {
        var model = Server();
        model.ReportError("Restore needs attention", "Archive hash mismatch", "Backups", "Open recovery in Backups");
        model.CompleteRefresh(true, now: Clock); model.ServerRunning = false;
        model.BeginOperation("Check game update", now: Clock);
        model.FinishOperation("Build information unavailable", false, Clock.AddSeconds(1));
        Assert.True(model.HasError); Assert.Equal("Backups", model.ErrorDestination);
        Assert.Equal("Archive hash mismatch", model.ErrorDetail);
        Assert.Contains("Failed", model.LastCompletion); Assert.DoesNotContain("✓", model.LastCompletion);
        Assert.DoesNotContain("preserved", model.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        model.ClearError(); Assert.False(model.HasError); Assert.Empty(model.ErrorDetail);
    }
}
