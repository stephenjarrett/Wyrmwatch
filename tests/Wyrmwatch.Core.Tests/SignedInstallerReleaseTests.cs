using System.Text.Json;
using Wyrmwatch.Core;

namespace Wyrmwatch.Core.Tests;

public sealed class SignedInstallerReleaseTests
{
    private static object Asset(string name, long size = 100) => new { name, size, digest = "sha256:" + new string('a', 64),
        browser_download_url = "https://github.com/stephenjarrett/Wyrmwatch/releases/download/v0.2.8/" + name };
    [Fact]
    public void InstalledWindowsUsesInstallerAndRequiresBoundedOfficialSignedCatalogAssets()
    {
        var assets = new[] { Asset("Wyrmwatch-0.2.8-win-x64-setup.exe"), Asset("Wyrmwatch-update.json"), Asset("Wyrmwatch-update.json.sig") };
        string Release(object[] files) => JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "v0.2.8", body = "fixture", assets = files });
        var release = AppUpdates.ParseRelease(Release(assets), new Version(0, 2, 7), "win-x64", true);
        Assert.NotNull(release?.Catalog);
        Assert.EndsWith("-setup.exe", release.Download);
        Assert.Throws<IOException>(() => AppUpdates.ParseRelease(Release(assets[..^1]), new Version(0, 2, 7), "win-x64", true));
        Assert.Throws<IOException>(() => AppUpdates.ParseRelease(Release([assets[0], Asset("Wyrmwatch-update.json", 65537), assets[2]]), new Version(0, 2, 7), "win-x64", true));
        Assert.Throws<IOException>(() => AppUpdates.ParseRelease(Release([assets[0], assets[1], Asset("Wyrmwatch-update.json.sig", 1025)]), new Version(0, 2, 7), "win-x64", true));
        Assert.Null(AppUpdates.ParseRelease(Release(assets), new Version(0, 2, 8), "win-x64", true));
    }
}
