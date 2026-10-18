using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CLIo.Services.Update;

public sealed record InstallLayout(bool CanSelfUpdate, string InstallDir, string Reason);

public sealed record UpdatePlan(bool Ok, string Message, UpdateManifest? Manifest, string ZipUrl);

/// <summary>Everything that can differ between the real app and a test.</summary>
public sealed class UpdateInstallerOptions
{
    public string PublicKeyBase64 { get; init; } = UpdateTrust.PublicKeyBase64;
    public string InstallDir { get; init; } = AppContext.BaseDirectory;
    public string DataDir { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLIo");
    public string ExeName { get; init; } = "CLIo.exe";
    public Version CurrentVersion { get; init; } = AppVersion.Parsed;
    public int ProcessId { get; init; } = Environment.ProcessId;
    public int WatchSeconds { get; init; } = 12;

    /// <summary>Extra hosts release files may come from (tests only; the signature is still required).</summary>
    public IReadOnlyCollection<string> ExtraTrustedHosts { get; init; } = [];

    /// <summary>What "close CLIo" means. Tests replace it so they do not exit the test process.</summary>
    public Action? Quit { get; init; }
}

/// <summary>
/// Installs a newer CLIo release. Nothing here runs unless the user approved (see InstallUpdateSkill).
///
/// Order of defenses: the release manifest must carry a valid signature from the maintainer's key; the
/// package must match the manifest's exact size and SHA-256; it is unpacked into a staging folder with
/// path-traversal and size checks and must contain CLIo.exe and a matching release marker; only then is
/// the database backed up and a separate PowerShell step swaps the files after CLIo has exited, keeps the
/// previous install, starts the new version, and restores the old one if the new one dies right away.
/// </summary>
public sealed class UpdateInstaller
{
    private const long MaxManifestBytes = 64 * 1024;
    private const long MaxUnpackedBytes = 3_000_000_000;

    private static readonly HttpClient Http = CreateHttp();

    private readonly UpdateService updates;
    private readonly DevLogService devLog;
    private readonly UpdateInstallerOptions options;
    private readonly RuntimeLog? runtimeLog;

    public UpdateInstaller(UpdateService updates, DevLogService devLog, UpdateInstallerOptions? options = null, RuntimeLog? runtimeLog = null)
    {
        this.runtimeLog = runtimeLog;
        this.updates = updates;
        this.devLog = devLog;
        this.options = options ?? new UpdateInstallerOptions();
    }

    /// <summary>Raised with short progress text such as "Downloading CLIo 0.5.0... 45%".</summary>
    public event Action<string>? Progress;

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CLIo/{AppVersion.Text}");
        return client;
    }

    // ---- can this copy update itself? ---------------------------------------------------------

    public InstallLayout GetLayout()
    {
        var dir = options.InstallDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!File.Exists(Path.Combine(dir, options.ExeName)) || !File.Exists(Path.Combine(dir, "release.json")))
        {
            return new InstallLayout(false, dir,
                "This copy of CLIo is running from source or a development build, not from an installed release, so it can't replace itself. Update it by pulling the latest code and rebuilding.");
        }

        try
        {
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new InstallLayout(false, dir,
                $"CLIo is installed in a folder this account can't write to ({dir}). Install it under your user profile (for example %LOCALAPPDATA%\\Programs\\CLIo) to allow self-updates.");
        }

        return new InstallLayout(true, dir, string.Empty);
    }

    // ---- plan (read-only: this is what the "dry run" shows) -----------------------------------

    public async Task<UpdatePlan> PlanAsync(CancellationToken cancellationToken = default)
    {
        var layout = GetLayout();
        if (!layout.CanSelfUpdate)
        {
            return Fail(layout.Reason);
        }

        var info = await updates.CheckAsync(force: true, cancellationToken);
        if (!info.Ok)
        {
            return Fail(info.Message);
        }

        if (info.NoReleases)
        {
            return Fail("No releases are published yet, so there is nothing to install.");
        }

        if (!info.IsNewer)
        {
            return Fail($"CLIo is already up to date (version {AppVersion.Text}).");
        }

        var assets = info.Assets ?? [];
        var manifestAsset = assets.FirstOrDefault(a => a.Name == "manifest.json");
        var signatureAsset = assets.FirstOrDefault(a => a.Name == "manifest.sig");
        if (manifestAsset is null || signatureAsset is null)
        {
            return Fail("The latest release has no signed manifest (manifest.json and manifest.sig), so it can't be installed automatically.");
        }

        if (!IsTrusted(manifestAsset.Url) || !IsTrusted(signatureAsset.Url))
        {
            return Fail("The release files are not hosted where I expect them, so I won't use them.");
        }

        byte[] manifestBytes;
        byte[] signatureBytes;
        try
        {
            manifestBytes = await DownloadSmallAsync(manifestAsset.Url, cancellationToken);
            var signatureText = Encoding.ASCII.GetString(await DownloadSmallAsync(signatureAsset.Url, cancellationToken)).Trim();
            signatureBytes = Convert.FromBase64String(signatureText);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or InvalidDataException)
        {
            return Fail($"Could not fetch the release manifest: {ex.Message}");
        }

        if (!UpdateTrust.Verify(manifestBytes, signatureBytes, options.PublicKeyBase64))
        {
            devLog.Log("Update rejected: manifest signature did not verify.", "update");
            runtimeLog?.Record("update", "reject", "blocked", "manifest signature did not verify against the built-in key");
            return Fail("The release's signature does not verify against my built-in key, so I won't install it. Nothing was changed.");
        }

        var manifest = UpdateManifest.TryParse(manifestBytes, out var reason);
        if (manifest is null)
        {
            return Fail(reason);
        }

        if (!Version.TryParse(manifest.Version, out var manifestVersion)
            || AppVersion.Normalize(manifestVersion) != info.Latest
            || AppVersion.Normalize(manifestVersion) <= AppVersion.Normalize(options.CurrentVersion))
        {
            return Fail("The signed manifest does not describe a newer version than this one, so I won't install it.");
        }

        var zip = assets.FirstOrDefault(a => a.Name == manifest.Asset);
        if (zip is null || !IsTrusted(zip.Url))
        {
            return Fail("The release package named in the signed manifest is missing or not hosted where I expect.");
        }

        return new UpdatePlan(true, string.Empty, manifest, zip.Url);
    }

    private static UpdatePlan Fail(string message) => new(false, message, null, string.Empty);

    /// <summary>The text shown to the user before they approve.</summary>
    public string Describe(UpdatePlan plan)
    {
        var m = plan.Manifest!;
        var sb = new StringBuilder();
        sb.AppendLine($"Install CLIo {m.Version} (you have {AppVersion.Text}).");
        sb.AppendLine();
        sb.AppendLine($"1. Download {m.Asset} ({m.Size / 1024.0 / 1024.0:0.#} MB) from this project's GitHub release.");
        sb.AppendLine($"2. Check that its size and SHA-256 ({m.Sha256[..12]}...) match the signed manifest. The signature already verified against CLIo's built-in key.");
        sb.AppendLine("3. Unpack it to a staging folder and check its contents.");
        sb.AppendLine("4. Back up your database.");
        sb.AppendLine("5. Close CLIo, keep a copy of the current version, replace the files, and start the new version.");
        sb.AppendLine("6. If the new version closes right after starting, restore the old one automatically.");
        sb.AppendLine();
        sb.AppendLine("Your data (memory, chats, calendar, settings) is not touched by the file swap.");
        if (!string.IsNullOrWhiteSpace(m.Notes))
        {
            sb.AppendLine();
            sb.AppendLine("What's new: " + (m.Notes.Length > 500 ? m.Notes[..500] + "..." : m.Notes));
        }

        return sb.ToString().TrimEnd();
    }

    // ---- stage (downloads and checks, still changes nothing that is installed) -----------------

    public async Task<string> StageAsync(UpdatePlan plan, CancellationToken cancellationToken = default)
    {
        var timer = runtimeLog?.Start("update", "stage");
        try
        {
            var staging = await StageCoreAsync(plan, cancellationToken);
            timer?.Done("ok", $"CLIo {plan.Manifest!.Version} downloaded, verified and unpacked");
            return staging;
        }
        catch (Exception ex)
        {
            timer?.Done("error", ex.Message);
            throw;
        }
    }

    private async Task<string> StageCoreAsync(UpdatePlan plan, CancellationToken cancellationToken)
    {
        var manifest = plan.Manifest ?? throw new InvalidOperationException("No plan.");
        var root = Path.Combine(options.DataDir, "updates", manifest.Version);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, manifest.Asset);
        var stagingDir = Path.Combine(root, "staging");

        await DownloadVerifiedAsync(plan.ZipUrl, zipPath, manifest, cancellationToken);

        Report($"Unpacking CLIo {manifest.Version}...");
        Unpack(zipPath, stagingDir);

        if (!File.Exists(Path.Combine(stagingDir, options.ExeName)))
        {
            throw new InvalidDataException($"The package does not contain {options.ExeName}.");
        }

        var markerPath = Path.Combine(stagingDir, "release.json");
        if (!File.Exists(markerPath))
        {
            throw new InvalidDataException("The package has no release marker.");
        }

        using (var marker = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath, cancellationToken)))
        {
            var packageVersion = marker.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (packageVersion != manifest.Version)
            {
                throw new InvalidDataException($"The package says it is version {packageVersion}, but the manifest says {manifest.Version}.");
            }
        }

        File.Delete(zipPath);
        return stagingDir;
    }

    private async Task DownloadVerifiedAsync(string url, string zipPath, UpdateManifest manifest, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        var lastReported = -1;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(zipPath))
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                received += read;
                if (received > manifest.Size)
                {
                    throw new InvalidDataException("The download is larger than the signed manifest says, so it was discarded.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                var percent = (int)(received * 100 / manifest.Size);
                if (percent != lastReported)
                {
                    lastReported = percent;
                    Report($"Downloading CLIo {manifest.Version}... {percent}%");
                }
            }
        }

        if (received != manifest.Size)
        {
            File.Delete(zipPath);
            throw new InvalidDataException("The download is smaller than the signed manifest says, so it was discarded.");
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, manifest.Sha256, StringComparison.Ordinal))
        {
            File.Delete(zipPath);
            devLog.Log("Update rejected: package SHA-256 did not match the manifest.", "update");
            throw new InvalidDataException("The download does not match the signed checksum, so it was discarded.");
        }
    }

    private static void Unpack(string zipPath, string stagingDir)
    {
        Directory.CreateDirectory(stagingDir);
        var rootFull = Path.GetFullPath(stagingDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(stagingDir, entry.FullName));
            if (!destination.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && destination + Path.DirectorySeparatorChar != rootFull)
            {
                throw new InvalidDataException($"The package contains an unsafe path ({entry.FullName}).");
            }

            total += entry.Length;
            if (total > MaxUnpackedBytes)
            {
                throw new InvalidDataException("The package unpacks to an unreasonable size.");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    // ---- apply (closes CLIo; the script swaps files and restarts) ------------------------------

    public void ApplyAndRestart(string stagingDir, UpdateManifest manifest)
    {
        var layout = GetLayout();
        if (!layout.CanSelfUpdate)
        {
            throw new InvalidOperationException(layout.Reason);
        }

        var updatesDir = Path.Combine(options.DataDir, "updates");
        Directory.CreateDirectory(updatesDir);

        BackupDatabase(manifest.Version);

        var scriptPath = Path.Combine(updatesDir, "apply-update.ps1");
        File.WriteAllText(scriptPath, UpdateApplyScript.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var resultFile = Path.Combine(updatesDir, "update-result.json");
        if (File.Exists(resultFile))
        {
            File.Delete(resultFile);
        }

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
            "-ProcessId", options.ProcessId.ToString(),
            "-InstallDir", layout.InstallDir,
            "-StagingDir", stagingDir,
            "-BackupDir", Path.Combine(updatesDir, "previous-install"),
            "-ResultFile", resultFile,
            "-ExeName", options.ExeName,
            "-NewVersion", manifest.Version,
            "-WatchSeconds", options.WatchSeconds.ToString(),
        })
        {
            psi.ArgumentList.Add(argument);
        }

        Process.Start(psi);
        devLog.Log($"Update to {manifest.Version} handed to the swap script; closing CLIo.", "update");
        runtimeLog?.Record("update", "apply", "ok", $"handed {manifest.Version} to the swap script; CLIo is closing");
        Report($"Installing CLIo {manifest.Version}: closing CLIo...");
        (options.Quit ?? App.Quit)();
    }

    private void BackupDatabase(string version)
    {
        try
        {
            var db = Path.Combine(options.DataDir, "clio.db");
            if (File.Exists(db))
            {
                var backups = Path.Combine(options.DataDir, "backups");
                Directory.CreateDirectory(backups);
                File.Copy(db, Path.Combine(backups, $"clio.db.pre-update-{version}-{DateTime.Now:yyyyMMdd-HHmmss}"), overwrite: false);
            }
        }
        catch (Exception ex)
        {
            // A failed backup must not be silent: stop the update rather than risk the data.
            throw new IOException($"Could not back up the database, so the update was not started: {ex.Message}", ex);
        }
    }

    /// <summary>The outcome of the last update attempt, shown once after CLIo restarts. Null if none.</summary>
    public (bool Ok, string Version, string Message)? ReadAndClearResult()
    {
        var path = Path.Combine(options.DataDir, "updates", "update-result.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // ReadAllText strips a UTF-8 byte-order mark, which Windows PowerShell 5.1 writes.
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var o) && o.GetBoolean();
            var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            File.Delete(path);
            return (ok, version, message);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private bool IsTrusted(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (options.ExtraTrustedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith($"/{UpdateService.Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> DownloadSmallAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxManifestBytes)
        {
            throw new InvalidDataException("A release file that should be tiny is too large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxManifestBytes)
            {
                throw new InvalidDataException("A release file that should be tiny is too large.");
            }
        }

        return buffer.ToArray();
    }

    private void Report(string text) => Progress?.Invoke(text);
}
