using System.Security.Cryptography;
using System.Text.Json;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;
using Wyrmwatch.Core;

namespace Wyrmwatch.Desktop;

internal sealed record SignedUpdateArtifact(string Name, long Bytes, string Sha256);
internal sealed record SignedUpdateCatalog(int Schema, string Version, string SourceCommit, SignedUpdateArtifact[] Artifacts)
{
    internal const int CatalogLimit = 64 * 1024;

    internal static SignedUpdateCatalog Verify(byte[] content, string signature, Version current)
    {
        using var stream = typeof(SignedUpdateCatalog).Assembly.GetManifestResourceStream("Wyrmwatch.UpdateSigning.pub")
            ?? throw new IOException("The application has no embedded update verification key. Nothing was installed.");
        using var reader = new StreamReader(stream);
        return Verify(content, signature, reader.ReadToEnd().Trim(), current);
    }

    // The key is supplied by the owner and embedded at build time. Never load a
    // verification key from downloaded metadata or a user-writable settings file.
    internal static SignedUpdateCatalog Verify(byte[] content, string signature, string publicKey, Version current)
    {
        if (content.Length == 0 || content.Length > CatalogLimit)
            throw new IOException("The signed update catalog has an unsupported size.");
        try
        {
            if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(signature) ||
                Convert.FromBase64String(publicKey).Length != 32 || Convert.FromBase64String(signature).Length != 64)
                throw new FormatException();
            var verifier = new Ed25519Checker(SecurityMode.Strict, publicKey, publicKeyFile: null);
            if (!verifier.HasValidKeyInformation() || verifier.VerifySignature(signature, content) != ValidationResult.Valid)
                throw new IOException("The update signature is invalid. Nothing was installed.");
        }
        catch (FormatException)
        {
            throw new IOException("The update signature or verification key is missing or invalid. Nothing was installed.");
        }
        SignedUpdateCatalog catalog;
        try { catalog = JsonSerializer.Deserialize<SignedUpdateCatalog>(content) ?? throw new JsonException(); }
        catch (JsonException) { throw new IOException("The signed update catalog is invalid."); }
        if (catalog.Schema != 1 || !System.Version.TryParse(catalog.Version, out var version) ||
            version.Build < 0 || version <= current || catalog.Version != version.ToString(3) ||
            catalog.SourceCommit is not { Length: 40 } || !catalog.SourceCommit.All(Uri.IsHexDigit) ||
            catalog.Artifacts is not { Length: > 0 and <= 8 })
            throw new IOException("The signed update catalog has an unsupported version or source.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in catalog.Artifacts)
        {
            if (artifact is null || string.IsNullOrEmpty(artifact.Name) ||
                artifact.Name.Length > 120 || !artifact.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ||
                artifact.Name.Contains("..", StringComparison.Ordinal) || !names.Add(artifact.Name) ||
                artifact.Bytes <= 0 || artifact.Bytes > AppUpdates.DownloadLimit ||
                artifact.Sha256 is not { Length: 64 } || !artifact.Sha256.All(Uri.IsHexDigit))
                throw new IOException("The signed update catalog contains an invalid package.");
        }
        return catalog;
    }

    internal SignedUpdateArtifact Require(string expectedName, AppRelease release)
    {
        if (Version != release.Version ||
            !Uri.TryCreate(release.Download, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "github.com" ||
            uri.AbsolutePath != $"/{AppUpdates.Repository}/releases/download/v{Version}/{expectedName}")
            throw new IOException("The signed update does not match the official release.");
        var artifact = Artifacts.SingleOrDefault(x => x.Name == expectedName);
        if (artifact is null || artifact.Bytes != release.Bytes ||
            !artifact.Sha256.Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The signed update does not match the published package.");
        return artifact;
    }

    internal static async Task VerifyPackageAsync(string path, SignedUpdateArtifact artifact, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        SafePaths.NoLinks(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (file.Length != artifact.Bytes ||
            !Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The update package does not match its signed catalog. Nothing was installed.");
    }
}
