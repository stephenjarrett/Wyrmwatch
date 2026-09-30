using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public class ExtensionSafetyTests
{
    [Fact]
    public async Task AppUpdateRejectsWrongHashesAndEscapingArchivePathsBeforeInstallation()
    {
        using var fixture = new Fixture(); var package = Path.Combine(fixture.Root, "update.zip");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escape.exe").Open()); writer.Write("invalid"); }
        await Assert.ThrowsAsync<IOException>(() => AppUpdates.VerifyAndExtractAsync(package, new string('0', 64), fixture.Root, "win-x64", "1.0.0"));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "unpacked")));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package)));
        await Assert.ThrowsAsync<IOException>(() => AppUpdates.VerifyAndExtractAsync(package, hash, fixture.Root, "win-x64", "1.0.0"));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escape.exe")));
    }
    [Fact]
    public async Task VerifiedUpdateStagesBesideExistingFiles()
    {
        using var fixture = new Fixture(); var package = Path.Combine(fixture.Root, "update.zip");
        var previous = Path.Combine(fixture.Root, "Wyrmwatch.exe"); File.WriteAllText(previous, "previous version");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            foreach (var name in new[] { "Wyrmwatch.exe", "agent/Wyrmwatch.Agent.exe", "LICENSE", "NOTICE", "Wyrmwatch-source.zip", "Wyrmwatch.runtimeconfig.json" })
            { using var writer = new StreamWriter(zip.CreateEntry("Wyrmwatch-win-x64/" + name).Open()); writer.Write("fixture"); }
        var result = await AppUpdates.VerifyAndExtractAsync(package, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))), fixture.Root, "win-x64", "1.0.0");
        Assert.Equal("fixture", File.ReadAllText(result.Executable)); Assert.Equal("previous version", File.ReadAllText(previous));
    }
    [Fact]
    public void ReleasesRequireOfficialDownloadsAndPublishedDigests()
    {
        object Release(string download, string digest) => new { draft = false, prerelease = false, tag_name = "v1.0.0", body = "Changes", assets = new[] { new { name = "Wyrmwatch-win-x64.zip", browser_download_url = download, digest, size = 500 } } };
        var url = "https://github.com/stephenjarrett/Wyrmwatch/releases/download/v1.0.0/Wyrmwatch-win-x64.zip";
        Assert.NotNull(AppUpdates.ParseRelease(JsonSerializer.Serialize(Release(url, "sha256:" + new string('a', 64))), new(0, 2, 0), "win-x64"));
        Assert.Throws<IOException>(() => AppUpdates.ParseRelease(JsonSerializer.Serialize(Release(url, "")), new(0, 2, 0), "win-x64"));
        Assert.Throws<IOException>(() => AppUpdates.ParseRelease(JsonSerializer.Serialize(Release("https://example.com/update.zip", "sha256:" + new string('a', 64))), new(0, 2, 0), "win-x64"));
    }
    [Fact]
    public void LanguagePacksAreDataOnlyAndRejectUnsafeIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => LanguagePack.Parse("{\"Id\":\"../other\",\"Name\":\"Test\",\"Strings\":{}}"));
        var pack = LanguagePack.Parse("{\"Id\":\"es\",\"Name\":\"Español\",\"Strings\":{\"Ui.123456789abc\":\"<script>plain text</script>\"}}");
        Assert.Equal("<script>plain text</script>", pack.Strings.Values.Single());
        Assert.Throws<ArgumentException>(() => LanguagePack.Parse("{\"Id\":\"es\",\"Name\":\"Test\",\"Strings\":{\"../bad\":\"invalid\"}}"));
    }
}
