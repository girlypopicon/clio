using System.Reflection;
using System.Text.Json;

namespace CLIo.Services;

/// <summary>The one place the app's version comes from: the project file's &lt;Version&gt;.</summary>
public static class AppVersion
{
    public static string Text { get; } = Read();

    public static Version Parsed { get; } = Version.TryParse(Text, out var v) ? Normalize(v) : new Version(0, 0, 0);

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static string Read()
    {
        var info = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            info = typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // "0.4.0+abc123" or "0.4.0-beta" -> "0.4.0"
        var end = info.IndexOfAny(['+', '-']);
        return end > 0 ? info[..end] : info;
    }
}

public sealed record UpdateInfo(
    bool Ok,
    string Message,
    bool NoReleases,
    bool IsNewer,
    string Tag,
    Version? Latest,
    string Url,
    string Notes,
    IReadOnlyList<CLIo.Services.Update.ReleaseAsset>? Assets = null);

/// <summary>
/// Looks at the repo's latest published GitHub release and says whether it is newer than this build.
/// It only ever READS the release list: it never downloads or installs anything. Applying an update is
/// a separate, approval-gated step that does not exist yet (it needs a release pipeline and checksums).
/// </summary>
public sealed class UpdateService
{
    public const string Repo = "girlypopicon/clio";

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string apiBase;
    private readonly RuntimeLog? runtimeLog;
    private UpdateInfo? cached;
    private DateTimeOffset cachedAt;

    /// <param name="apiBase">Only for tests: a stand-in for api.github.com. The CLIO_UPDATE_FEED environment variable does the same.
    /// It cannot weaken anything that matters: an installer still refuses releases whose signature does not verify.</param>
    public UpdateService(string? apiBase = null, RuntimeLog? runtimeLog = null)
    {
        this.runtimeLog = runtimeLog;
        this.apiBase = (apiBase ?? Environment.GetEnvironmentVariable("CLIO_UPDATE_FEED") ?? "https://api.github.com").TrimEnd('/');
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CLIo/{AppVersion.Text}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<UpdateInfo> CheckAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && cached is not null && DateTimeOffset.Now - cachedAt < CacheFor)
        {
            return cached;
        }

        var timer = runtimeLog?.Start("update", "check");
        try
        {
            using var response = await http.GetAsync($"{apiBase}/repos/{Repo}/releases/latest", cancellationToken);
            UpdateInfo result;
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                result = new UpdateInfo(true, "No releases have been published yet.", NoReleases: true, IsNewer: false, "", null, "", "");
            }
            else if (!response.IsSuccessStatusCode)
            {
                timer?.Done("error", $"GitHub answered {(int)response.StatusCode}");
                return new UpdateInfo(false, $"GitHub answered {(int)response.StatusCode}; try again later.", false, false, "", null, "", "");
            }
            else
            {
                result = Parse(await response.Content.ReadAsStringAsync(cancellationToken), AppVersion.Parsed);
            }

            cached = result;
            cachedAt = DateTimeOffset.Now;
            timer?.Done("ok", result.NoReleases ? "no releases published" : result.IsNewer ? $"newer release {result.Tag} available" : $"up to date (latest {result.Tag})");
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            timer?.Done("error", "could not reach GitHub");
            return new UpdateInfo(false, "Couldn't reach GitHub to check for updates.", false, false, "", null, "", "");
        }
    }

    internal static UpdateInfo Parse(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            if (notes.Length > 800)
            {
                notes = notes[..800] + "...";
            }

            // Only ever offer a link that points at this repo on github.com.
            if (!IsRepoUrl(url))
            {
                url = $"https://github.com/{Repo}/releases";
            }

            var text = tag.TrimStart('v', 'V');
            var end = text.IndexOfAny(['-', '+']);
            if (end > 0)
            {
                text = text[..end];
            }

            if (!Version.TryParse(text, out var parsed))
            {
                return new UpdateInfo(false, $"The latest release tag \"{tag}\" is not a version number.", false, false, tag, null, url, notes);
            }

            var assets = new List<CLIo.Services.Update.ReleaseAsset>();
            if (root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assetArray.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var link = a.TryGetProperty("browser_download_url", out var l) ? l.GetString() ?? "" : "";
                    var size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var bytes) ? bytes : 0;
                    if (name.Length > 0 && link.Length > 0)
                    {
                        assets.Add(new CLIo.Services.Update.ReleaseAsset(name, link, size));
                    }
                }
            }

            var latest = AppVersion.Normalize(parsed);
            var newer = latest > AppVersion.Normalize(current);
            return new UpdateInfo(true, newer ? $"Version {latest} is available." : "CLIo is up to date.", false, newer, tag, latest, url, notes, assets);
        }
        catch (JsonException)
        {
            return new UpdateInfo(false, "GitHub's answer could not be read.", false, false, "", null, "", "");
        }
    }

    public static bool IsRepoUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith($"/{Repo}/", StringComparison.OrdinalIgnoreCase);

    /// <summary>What CLIo tells the user. Never claims an update was applied.</summary>
    public static string Describe(UpdateInfo info)
    {
        var current = AppVersion.Text;
        if (!info.Ok)
        {
            return $"{info.Message} (This is version {current}.)";
        }

        if (info.NoReleases)
        {
            return $"This is version {current}. No releases are published on GitHub yet, so there is nothing to compare against. " +
                   "If you run CLIo from source, updating means pulling the latest code and rebuilding. I can't install updates myself.";
        }

        if (!info.IsNewer)
        {
            return $"This is version {current} and the latest release is {info.Tag}. CLIo is up to date.";
        }

        var notes = string.IsNullOrWhiteSpace(info.Notes) ? string.Empty : $"\nWhat's new: {info.Notes.Trim()}";
        return $"Version {info.Latest} is available (this is {current}).{notes}\nRelease page: {info.Url}\n" +
               "To install it, ask me to install the update (you will be shown exactly what happens and asked to approve), or press Install in the banner. If CLIo is run from source, pull the code and rebuild instead.";
    }
}
