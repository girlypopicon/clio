using System.Security.Cryptography;
using System.Text.Json;

namespace CLIo.Services.Update;

/// <summary>
/// Decides whether a release is genuine. Each release publishes manifest.json (version, file name, size,
/// SHA-256) and manifest.sig, an ECDSA P-256 signature over the exact manifest bytes made with a private key
/// that exists only on the maintainer's PC. CLIo carries the matching public key and installs nothing whose
/// signature does not verify, so a hijacked GitHub account or a swapped download cannot put code on this PC.
/// </summary>
public static class UpdateTrust
{
    /// <summary>Base64 SubjectPublicKeyInfo of the release-signing key (see tools/New-ReleaseKey.ps1).</summary>
    public const string PublicKeyBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvOk6FoOQl3ri/YWyRAm0MihZdqGjbeeKbm+VsYVu7YUpveWde34M/oig5/RqcLgxIYwiYb9Z2qHDsu9/kYoZUA==";

    public static bool Verify(byte[] manifest, byte[] signature, string? publicKeyBase64 = null)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64 ?? PublicKeyBase64), out _);
            return key.VerifyData(manifest, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}

public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>The signed description of one release.</summary>
public sealed record UpdateManifest(
    string App,
    string Version,
    string Asset,
    long Size,
    string Sha256,
    string Notes)
{
    private const long MaxPackageBytes = 1_500_000_000;

    /// <summary>Parses and validates the signed manifest. Returns null (with a reason) if anything looks off.</summary>
    public static UpdateManifest? TryParse(byte[] json, out string reason)
    {
        reason = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

            var manifest = new UpdateManifest(
                Text("app"),
                Text("version"),
                Text("asset"),
                root.TryGetProperty("size", out var size) && size.TryGetInt64(out var s) ? s : 0,
                Text("sha256").ToLowerInvariant(),
                Text("notes"));

            if (manifest.App != "CLIo")
            {
                reason = "The manifest is not for CLIo.";
            }
            else if (!System.Version.TryParse(manifest.Version, out _))
            {
                reason = "The manifest version is not a version number.";
            }
            else if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Asset, @"^CLIo-[0-9][0-9A-Za-z.\-]*-win-x64\.zip$"))
            {
                reason = "The manifest names an unexpected file.";
            }
            else if (manifest.Size <= 0 || manifest.Size > MaxPackageBytes)
            {
                reason = "The manifest gives an unreasonable file size.";
            }
            else if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Sha256, "^[0-9a-f]{64}$"))
            {
                reason = "The manifest checksum is malformed.";
            }

            return reason.Length == 0 ? manifest : null;
        }
        catch (JsonException)
        {
            reason = "The manifest could not be read.";
            return null;
        }
    }
}
