using System.Security.Cryptography;
using System.Text.Json;
using Chaos.NaCl;
using Wyrmwatch.Core;
using Wyrmwatch.Desktop;

namespace Wyrmwatch.Desktop.Tests;

public sealed class SignedUpdateCatalogTests
{
    // Per-test random keys exist only in memory; no production signing key is
    // generated, read, written, logged, or checked into the repository.
    private sealed class SignedFixture
    {
        internal readonly byte[] Seed = RandomNumberGenerator.GetBytes(32);
        internal string PublicKey => Convert.ToBase64String(Ed25519.PublicKeyFromSeed(Seed));
        internal string Sign(byte[] bytes) => Convert.ToBase64String(Ed25519.Sign(bytes, Ed25519.ExpandedPrivateKeyFromSeed(Seed)));
        internal readonly byte[] Package = "a packaged application fixture"u8.ToArray();
        internal SignedUpdateCatalog Catalog => new(1, "0.2.8", new string('a', 40),
            [new("Wyrmwatch-0.2.8-win-x64.msi", Package.Length, Convert.ToHexString(SHA256.HashData(Package)))]);
        internal byte[] Content => JsonSerializer.SerializeToUtf8Bytes(Catalog);
    }

    [Fact]
    public async Task SignedCatalogBindsExactReleaseBytesAndSource()
    {
        var fixture = new SignedFixture();
        using var cancellation = new CancellationTokenSource();
        var catalog = SignedUpdateCatalog.Verify(fixture.Content, fixture.Sign(fixture.Content), fixture.PublicKey, new Version(0, 2, 7));
        var artifact = catalog.Artifacts[0];
        var release = new AppRelease("0.2.8", "", "", $"https://github.com/{AppUpdates.Repository}/releases/download/v0.2.8/{artifact.Name}", artifact.Sha256, artifact.Bytes);
        Assert.Equal(artifact, catalog.Require(artifact.Name, release));
        var directory = Path.Combine(Path.GetTempPath(), "wyrmwatch-signature-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, artifact.Name);
            await File.WriteAllBytesAsync(file, fixture.Package, cancellation.Token);
            await SignedUpdateCatalog.VerifyPackageAsync(file, artifact, cancellation.Token);
            var changed = fixture.Package.ToArray(); changed[0] ^= 1;
            await File.WriteAllBytesAsync(file, changed, cancellation.Token);
            await Assert.ThrowsAsync<IOException>(() => SignedUpdateCatalog.VerifyPackageAsync(file, artifact, cancellation.Token));
            await File.WriteAllBytesAsync(file, fixture.Package[..^1], cancellation.Token);
            await Assert.ThrowsAsync<IOException>(() => SignedUpdateCatalog.VerifyPackageAsync(file, artifact, cancellation.Token));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SignedUpdateCatalog.VerifyPackageAsync(file, artifact, cancellation.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void MissingMalformedWrongKeyAndTamperedSignaturesFailClosed()
    {
        var fixture = new SignedFixture(); var other = new SignedFixture();
        var content = fixture.Content; var signature = fixture.Sign(content);
        foreach (var invalid in new[] { "", "not-base64", Convert.ToBase64String(new byte[64]) })
            Assert.Throws<IOException>(() => SignedUpdateCatalog.Verify(content, invalid, fixture.PublicKey, new Version(0, 2, 7)));
        foreach (var invalid in new[] { "", "not-base64", other.PublicKey })
            Assert.Throws<IOException>(() => SignedUpdateCatalog.Verify(content, signature, invalid, new Version(0, 2, 7)));
        content[content.Length / 2] ^= 1;
        Assert.Throws<IOException>(() => SignedUpdateCatalog.Verify(content, signature, fixture.PublicKey, new Version(0, 2, 7)));
    }

    [Fact]
    public void ProductionTrustEmbedsOwnersPublicKeyAndRejectsTestKey()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("Wyrmwatch.UpdateSigning.pub");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var bytes = Convert.FromBase64String(reader.ReadToEnd().Trim());
        Assert.Equal(32, bytes.Length);
        Assert.Equal("F55BC01F275DBF16922F6796CAD810B8EBE448B68AB12C13EA9DE1513E042F52", Convert.ToHexString(SHA256.HashData(bytes)));
        var fixture = new SignedFixture();
        Assert.Throws<IOException>(() => SignedUpdateCatalog.Verify(fixture.Content, fixture.Sign(fixture.Content), new Version(0, 2, 7)));
    }

    [Fact]
    public void SignedButUnsafeOrStaleMetadataIsRejected()
    {
        var fixture = new SignedFixture();
        var catalog = fixture.Catalog; var artifact = catalog.Artifacts[0];
        foreach (var invalid in new[] {
            catalog with { Schema = 2 }, catalog with { Version = "0.2.7" }, catalog with { Version = "0.2.8.0" }, catalog with { Version = "1.2" },
            catalog with { SourceCommit = "unknown" }, catalog with { Artifacts = null! },
            catalog with { Artifacts = [artifact, artifact] },
            catalog with { Artifacts = [artifact with { Name = "../payload.exe" }] },
            catalog with { Artifacts = [artifact with { Bytes = AppUpdates.DownloadLimit + 1 }] },
            catalog with { Artifacts = [artifact with { Sha256 = new string('z', 64) }] } })
        {
            var content = JsonSerializer.SerializeToUtf8Bytes(invalid);
            Assert.Throws<IOException>(() => SignedUpdateCatalog.Verify(content, fixture.Sign(content), fixture.PublicKey, new Version(0, 2, 7)));
        }
        var oversized = new byte[SignedUpdateCatalog.CatalogLimit + 1];
        Assert.Throws<IOException>(() => SignedUpdateCatalog.Verify(oversized, fixture.Sign(oversized), fixture.PublicKey, new Version(0, 2, 7)));
    }

    [Fact]
    public void SignedCatalogCannotAuthorizeDifferentRepositoryVersionOrPackage()
    {
        var fixture = new SignedFixture(); var catalog = fixture.Catalog; var artifact = catalog.Artifacts[0];
        var release = new AppRelease("0.2.8", "", "", $"https://github.com/{AppUpdates.Repository}/releases/download/v0.2.8/{artifact.Name}", artifact.Sha256, artifact.Bytes);
        foreach (var invalid in new[] { release with { Version = "0.2.9" }, release with { Bytes = artifact.Bytes + 1 },
            release with { Digest = new string('0', 64) }, release with { Download = release.Download.Replace("stephenjarrett", "someone-else") },
            release with { Download = release.Download.Replace("https:", "http:") } })
            Assert.Throws<IOException>(() => catalog.Require(artifact.Name, invalid));
        Assert.Throws<IOException>(() => catalog.Require("other.exe", release));
    }
}
