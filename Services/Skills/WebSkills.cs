using System.Net;
using System.Text;
using HtmlAgilityPack;

namespace CLIo.Services.Skills;

/// <summary>web_search: ask the web a question, get back titles, links and snippets.</summary>
public sealed class WebSearchSkill : ISkill
{
    private readonly WebSearchService search;

    public WebSearchSkill(WebSearchService search) => this.search = search;

    public string Name => "web_search";

    public string Summary => "search the web; returns titles, URLs and short snippets";

    public string InputHint => "<search query>";

    public SkillPermission Permission => SkillPermission.ReadOnly;

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public string DescribeRun(string input) => $"Searching the web for \"{Shorten(input)}\"...";

    public async Task<SkillResult> RunAsync(string input, CancellationToken cancellationToken)
    {
        var results = await search.SearchAsync(input, 6, cancellationToken);
        if (results.Count == 0)
        {
            return SkillResult.Fail("No search results came back. The search service may be unreachable or may have changed. Say so rather than guessing.");
        }

        var sb = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {results[i].Text}");
            sb.AppendLine($"   {results[i].Url}");
        }

        return new SkillResult(true, sb.ToString().TrimEnd(), results.Select(r => r.Url).ToList());
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "...";
}

/// <summary>browse: fetch one web page and return its readable text. No scripts are run.</summary>
public sealed class BrowseSkill : ISkill
{
    private const int MaxDownloadBytes = 2 * 1024 * 1024;
    private const int MaxTextChars = 6000;

    private static readonly HttpClient Http = new(UrlSafety.CreateSafeHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    public string Name => "browse";

    public string Summary => "open one web page and read its text (no logins, no scripts, no downloads)";

    public string InputHint => "<full https:// URL>";

    public SkillPermission Permission => SkillPermission.ReadOnly;

    public TimeSpan Timeout => TimeSpan.FromSeconds(20);

    public string DescribeRun(string input) => Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
        ? $"Reading {uri.Host}..."
        : "Reading a page...";

    public async Task<SkillResult> RunAsync(string input, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || !UrlSafety.IsAllowedScheme(uri))
        {
            return SkillResult.Fail("That is not a full http(s) URL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.8,application/json;q=0.5");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return SkillResult.Fail($"Could not open the page: {ex.Message}");
        }

        using (response)
        {
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            if (!response.IsSuccessStatusCode)
            {
                return SkillResult.Fail($"The page answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "text/html";
            var isHtml = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);
            var isText = mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase);
            if (!isHtml && !isText)
            {
                return SkillResult.Fail($"That link is a {mediaType} file, not a readable page.");
            }

            var bytes = await ReadLimitedAsync(response, cancellationToken);
            var title = string.Empty;
            var text = isHtml ? ExtractReadableText(bytes, out title) : Encoding.UTF8.GetString(bytes);

            text = Collapse(text);
            var truncated = text.Length > MaxTextChars;
            if (truncated)
            {
                text = text[..MaxTextChars];
            }

            var header = $"URL: {finalUri}" + (string.IsNullOrWhiteSpace(title) ? "" : $"\nTitle: {title}");
            if (text.Length < 40)
            {
                return SkillResult.Fail($"{header}\nThe page has almost no readable text (it may need JavaScript or a login).");
            }

            return new SkillResult(true, $"{header}\n\n{text}{(truncated ? "\n[page truncated]" : "")}", [finalUri.ToString()]);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length >= MaxDownloadBytes)
            {
                break;
            }
        }

        return buffer.ToArray();
    }

    internal static string ExtractReadableText(byte[] html, out string title)
    {
        var doc = new HtmlDocument();
        doc.Load(new MemoryStream(html), detectEncodingFromByteOrderMarks: true);

        title = WebUtility.HtmlDecode(doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? string.Empty).Trim();

        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//noscript|//svg|//iframe|//form|//nav|//footer|//aside|//header|//template")?.ToList() ?? [])
        {
            node.Remove();
        }

        var root = doc.DocumentNode.SelectSingleNode("//article")
            ?? doc.DocumentNode.SelectSingleNode("//main")
            ?? doc.DocumentNode.SelectSingleNode("//body")
            ?? doc.DocumentNode;

        var sb = new StringBuilder();
        Walk(root, sb);
        return sb.ToString();
    }

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "li", "ul", "ol", "br", "tr", "table", "pre", "blockquote",
        "h1", "h2", "h3", "h4", "h5", "h6", "hr", "dd", "dt", "dl", "figcaption"
    };

    private static void Walk(HtmlNode node, StringBuilder sb)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            sb.Append(WebUtility.HtmlDecode(node.InnerText));
            return;
        }

        if (node.NodeType == HtmlNodeType.Comment)
        {
            return;
        }

        foreach (var child in node.ChildNodes)
        {
            Walk(child, sb);
        }

        if (BlockElements.Contains(node.Name))
        {
            sb.Append('\n');
        }
    }

    private static string Collapse(string text)
    {
        var lines = text.Replace("\r", "")
            .Split('\n')
            .Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(line => line.Length > 0);
        return string.Join('\n', lines);
    }
}
