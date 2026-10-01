using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Wyrmwatch.Core;
using Wyrmwatch.Desktop;

namespace Wyrmwatch.Desktop.Tests;

public class AppUpdateFlowsTests
{
    [AvaloniaFact]
    public async Task ByteProgressNeverEnablesHandoffBeforeVerificationAndReadyPreservesCurrentApp()
    {
        using var fixture = new Fixture();
        var stream = fixture.ConfigureDownload();
        var download = fixture.Window.DownloadManagerUpdateAsync();
        try
        {
            await Until(() => stream.Reads == 1);
            Assert.True(fixture.Progress.IsIndeterminate);
            Assert.Equal(0, fixture.Progress.Value);
            Assert.True(fixture.Cancel.IsEnabled);
            Assert.False(fixture.Check.IsEnabled);
            Assert.False(fixture.Download.IsEnabled);
            Assert.False(fixture.Switch.IsEnabled);
            var timing = Field<TextBlock>(fixture.Window, "AppUpdateTiming").Text;
            Assert.Contains("Elapsed", timing);
            Assert.Contains("Last activity", timing);

            stream.First.TrySetResult();
            await Until(() => fixture.Progress.Value > 0);
            Assert.InRange(fixture.Progress.Value, 1, 99);
            Assert.False(fixture.Progress.IsIndeterminate);
            Assert.Contains("package bytes received", fixture.Status.Text);
            Assert.False(fixture.Switch.IsEnabled);

            stream.Second.TrySetResult();
            await Until(() => fixture.Progress.Value == 100);
            Assert.True(fixture.Progress.IsIndeterminate);
            Assert.Contains("awaiting verification", fixture.Status.Text);
            Assert.False(fixture.Switch.IsEnabled);
            Assert.True(fixture.Cancel.IsEnabled);

            stream.Complete.TrySetResult();
            await download;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("verified and ready", fixture.Status.Text);
            Assert.True(fixture.Switch.IsEnabled);
            Assert.True(fixture.Check.IsEnabled);
            Assert.False(fixture.Cancel.IsEnabled);
            Assert.False(fixture.Progress.IsVisible);
            Assert.Contains("Finished in", Field<TextBlock>(fixture.Window, "AppUpdateTiming").Text);
            Assert.Equal("current app fixture", File.ReadAllText(fixture.PreviousApp));
        }
        finally { stream.ReleaseAll(); await download; }
    }

    [AvaloniaFact]
    public async Task CancelThenFailedChecksumLeavesSafeRetryAndPersistentActionableError()
    {
        using var fixture = new Fixture();
        var stream = fixture.ConfigureDownload();
        var download = fixture.Window.DownloadManagerUpdateAsync();
        try
        {
            await Until(() => stream.Reads == 1);
            fixture.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(fixture.Cancel.IsEnabled);
            await download;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("cancelled", fixture.Status.Text);
            Assert.True(fixture.Download.IsEnabled);
            Assert.True(fixture.Check.IsEnabled);
            Assert.False(fixture.Switch.IsEnabled);
            Assert.False(fixture.Model.HasError);

            stream = fixture.ConfigureDownload(invalidDigest: true);
            download = fixture.Window.DownloadManagerUpdateAsync();
            stream.ReleaseAll();
            await download;
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Model.HasError);
            Assert.Contains("checksum did not match", fixture.Model.ErrorDetail);
            Assert.Contains("current app is unchanged", fixture.Model.ErrorSummary);
            Assert.Equal("App updates", fixture.Model.ErrorDestination);
            Assert.True(fixture.Download.IsEnabled);
            Assert.True(fixture.Check.IsEnabled);
            Assert.False(fixture.Switch.IsEnabled);
            Assert.False(fixture.Cancel.IsEnabled);
            Assert.False(fixture.Progress.IsVisible);
            Assert.Equal("current app fixture", File.ReadAllText(fixture.PreviousApp));
        }
        finally { stream.ReleaseAll(); await download; }
    }

    private static T Field<T>(Window window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20, deadline.Token); }
        Dispatcher.UIThread.RunJobs();
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "wyrmwatch-ui-update-" + Guid.NewGuid().ToString("N"));
        private readonly byte[] package;
        public MainWindow Window { get; }
        public WorkspaceModel Model => (WorkspaceModel)Window.DataContext!;
        public ProgressBar Progress => Field<ProgressBar>(Window, "AppDownloadProgress");
        public TextBlock Status => Field<TextBlock>(Window, "AppUpdateStatus");
        public Button Cancel => Field<Button>(Window, "CancelAppDownloadButton");
        public Button Check => Field<Button>(Window, "CheckAppButton");
        public Button Download => Field<Button>(Window, "DownloadAppButton");
        public Button Switch => Field<Button>(Window, "SwitchAppButton");
        public string PreviousApp => Path.Combine(root, "current-app.fixture");
        public Fixture()
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(PreviousApp, "current app fixture");
            Program.HeadlessTest = true; Program.Demo = false; Program.DataDirectory = root;
            package = Package();
            Window = new MainWindow(); Window.Show(); Window.ShowPage(WorkspacePage.AppUpdates);
            Assert.True(Check.IsEnabled);
            Assert.False(Download.IsEnabled);
            Assert.False(Switch.IsEnabled);
            Assert.False(Cancel.IsEnabled);
            Assert.Contains("Check for an app update first", Field<TextBlock>(Window, "AppUpdateHint").Text);
        }
        public ControlledStream ConfigureDownload(bool invalidDigest = false)
        {
            var stream = new ControlledStream(package);
            Window.AppUpdateClientFactory = () => new AppUpdates(() => new HttpClient(new ResponseHandler(stream)));
            var digest = invalidDigest ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(package));
            var release = new AppRelease("9.0.0", "Offline fixture", "", "https://github.com/stephenjarrett/Wyrmwatch/releases/download/v9.0.0/package", digest, package.Length);
            typeof(MainWindow).GetField("appRelease", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, release);
            return stream;
        }
        public void Dispose() { Window.Close(); Directory.Delete(root, true); }
        private static byte[] Package()
        {
            using var output = new MemoryStream();
            var windows = OperatingSystem.IsWindows();
            var runtime = windows ? "win-x64" : "linux-x64";
            var files = new[] { windows ? "Wyrmwatch.exe" : "Wyrmwatch", windows ? "agent/Wyrmwatch.Agent.exe" : "agent/Wyrmwatch.Agent", "LICENSE", "NOTICE", "Wyrmwatch-source.zip", "Wyrmwatch.runtimeconfig.json" };
            if (windows)
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var name in files)
                {
                    using var entry = archive.CreateEntry("Wyrmwatch-" + runtime + "/" + name).Open();
                    entry.Write("offline fixture; never execute"u8);
                }
            }
            else
            {
                using var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true);
                using var archive = new TarWriter(gzip);
                foreach (var name in files)
                {
                    using var data = new MemoryStream("offline fixture; never execute"u8.ToArray());
                    archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Wyrmwatch-" + runtime + "/" + name) { DataStream = data });
                }
            }
            return output.ToArray();
        }
    }
    private sealed class ResponseHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
    }
    private sealed class ControlledStream(byte[] bytes) : Stream
    {
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads, position;
        public int Reads => Volatile.Read(ref reads);
        public void ReleaseAll() { First.TrySetResult(); Second.TrySetResult(); Complete.TrySetResult(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = Interlocked.Increment(ref reads);
            await (read == 1 ? First.Task : read == 2 ? Second.Task : Complete.Task).WaitAsync(cancellationToken);
            if (position >= bytes.Length) return 0;
            var count = Math.Min(buffer.Length, read == 1 ? bytes.Length / 2 : bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer); position += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
