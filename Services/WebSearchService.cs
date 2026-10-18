using System.Net;
using HtmlAgilityPack;

namespace CLIo.Services;

/// <summary>
/// Web search through DuckDuckGo's plain HTML results page (no API key). The older "instant answer"
/// API returned nothing for ordinary queries. HTML scraping is brittle by nature: if DuckDuckGo changes
/// the page, <see cref="SearchAsync"/> returns no results and the skill says so instead of guessing.
/// </summary>
public sealed class WebSearchService
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(12) };

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults = 6, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query.Trim())}");
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(await response.Content.ReadAsStringAsync(cancellationToken));
            return Parse(doc, maxResults);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<SearchResult> Parse(HtmlDocument doc, int maxResults)
    {
        var results = new List<SearchResult>();
        var links = doc.DocumentNode.SelectNodes("//a[contains(concat(' ', normalize-space(@class), ' '), ' result__a ')]");
        if (links is null)
        {
            return results;
        }

        foreach (var link in links)
        {
            var container = link.Ancestors("div")
                .FirstOrDefault(div => div.GetAttributeValue("class", "").Split(' ').Contains("result"));
            if (container is not null && container.GetAttributeValue("class", "").Contains("result--ad"))
            {
                continue; // advertisement
            }

            var url = ResolveTarget(link.GetAttributeValue("href", ""));
            if (url is null)
            {
                continue;
            }

            var title = WebUtility.HtmlDecode(link.InnerText).Trim();
            var snippet = container?.SelectSingleNode(".//a[contains(@class,'result__snippet')]")?.InnerText;
            snippet = snippet is null ? "" : WebUtility.HtmlDecode(snippet).Trim();

            var text = snippet.Length > 0 ? $"{title} - {snippet}" : title;
            results.Add(new SearchResult(text, url, "DuckDuckGo"));
            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>DuckDuckGo wraps links as //duckduckgo.com/l/?uddg=&lt;encoded target&gt;. Returns the real target, or null.</summary>
    private static string? ResolveTarget(string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            href = "https:" + href;
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                if (pair.StartsWith("uddg=", StringComparison.Ordinal))
                {
                    var target = Uri.UnescapeDataString(pair[5..]);
                    return Uri.TryCreate(target, UriKind.Absolute, out var t) && t.Scheme is "http" or "https" ? target : null;
                }
            }

            return null; // other duckduckgo.com links (ads, y.js) are not results
        }

        return uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }
}

public sealed record SearchResult(string Text, string Url, string Source);
