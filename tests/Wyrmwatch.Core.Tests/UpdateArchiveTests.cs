using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class UpdateArchiveTests
{
    private static string Package(Fixture fixture, Action<TarWriter>? extra = null)
    {
        var package = Path.Combine(fixture.Root, "update.tar.gz");
        using var file = File.Create(package); using var gzip = new GZipStream(file, CompressionMode.Compress); using var tar = new TarWriter(gzip);
        foreach (var name in new[] { "Wyrmwatch", "agent/Wyrmwatch.Agent", "LICENSE", "NOTICE", "Wyrmwatch-source.zip", "Wyrmwatch.runtimeconfig.json" })
        {
            using var data = new MemoryStream("fixture"u8.ToArray());
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Wyrmwatch-linux-x64/" + name) { DataStream = data });
        }
        extra?.Invoke(tar); return package;
    }
    private static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task LinuxPackageExtractsWithExecutablePermissionsAndKeepsPreviousVersion()
    {
        using var f = new Fixture(); var previous = Path.Combine(f.Root, "Wyrmwatch"); File.WriteAllText(previous, "previous");
        var package = Package(f);
        var result = await AppUpdates.VerifyAndExtractAsync(package, Digest(package), f.Root, "linux-x64", "1.0.0");
        Assert.Equal("fixture", File.ReadAllText(result.Executable)); Assert.Equal("previous", File.ReadAllText(previous));
        if (!OperatingSystem.IsWindows())
            foreach (var executable in new[] { result.Executable, Path.Combine(result.Directory, "agent", "Wyrmwatch.Agent") })
                Assert.True(File.GetUnixFileMode(executable).HasFlag(UnixFileMode.UserExecute));
    }

    [Theory]
    [InlineData("symbolic link")]
    [InlineData("hard link")]
    [InlineData("escape")]
    [InlineData("duplicate")]
    [InlineData("truncated")]
    public async Task UnsafeOrIncompleteLinuxPackagesNeverReplaceTheCurrentApp(string failure)
    {
        using var f = new Fixture(); var previous = Path.Combine(f.Root, "Wyrmwatch"); File.WriteAllText(previous, "previous");
        var package = Package(f, tar =>
        {
            if (failure is "symbolic link" or "hard link")
                tar.WriteEntry(new PaxTarEntry(failure == "symbolic link" ? TarEntryType.SymbolicLink : TarEntryType.HardLink, "Wyrmwatch-linux-x64/linked") { LinkName = "../../outside" });
            else if (failure != "truncated")
            {
                using var data = new MemoryStream("unsafe"u8.ToArray());
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, failure == "escape" ? "../escaped" : "Wyrmwatch-linux-x64/Wyrmwatch") { DataStream = data });
            }
        });
        if (failure == "truncated") { var bytes = File.ReadAllBytes(package); File.WriteAllBytes(package, bytes[..(bytes.Length / 2)]); }
        await Assert.ThrowsAnyAsync<IOException>(() => AppUpdates.VerifyAndExtractAsync(package, Digest(package), f.Root, "linux-x64", "1.0.0"));
        Assert.Equal("previous", File.ReadAllText(previous)); Assert.False(File.Exists(Path.Combine(f.Root, "escaped")));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("oversized")]
    [InlineData("untrusted redirect")]
    [InlineData("cancelled")]
    public async Task FailedDownloadsNeverReachExtractionOrChangeCurrentApp(string failure)
    {
        using var f = new Fixture(); var previous = Path.Combine(f.Root, "Wyrmwatch.exe"); File.WriteAllText(previous, "previous");
        using var cancel = new CancellationTokenSource();
        var updater = new AppUpdates(() => new HttpClient(new ResponseHandler(() =>
        {
            if (failure == "cancelled") cancel.Cancel();
            if (failure == "untrusted redirect") return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://untrusted.example/package") } };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[failure == "oversized" ? 101 : 99]) };
        })));
        var release = new AppRelease("1.0.0", "", "", "https://github.com/stephenjarrett/Wyrmwatch/releases/download/v1.0.0/package.zip", new string('0', 64), 100);
        if (failure == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.DownloadAsync(release, f.Root, "win-x64", token: cancel.Token));
        else await Assert.ThrowsAsync<IOException>(() => updater.DownloadAsync(release, f.Root, "win-x64"));
        Assert.Empty(Directory.GetDirectories(f.Root, "unpacked", SearchOption.AllDirectories));
        Assert.Equal("previous", File.ReadAllText(previous));
    }
    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response());
    }
}
