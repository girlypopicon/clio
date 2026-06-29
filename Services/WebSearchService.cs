using System.Net.Http.Json;
using System.Text.Json;

namespace CLIo.Services;

public sealed class WebSearchService
{
    private readonly HttpClient httpClient = new();

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int maxResults = 5)
    {
        // Uses DuckDuckGo instant answer API (no API key needed)
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var url = $"https://api.duckduckgo.com/?q={encoded}&format=json&no_html=1&skip_disambig=1";
            var response = await httpClient.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);
            var results = new List<SearchResult>();

            // Abstract (main result)
            var abstractText = doc.RootElement.GetProperty("AbstractText").GetString();
            var abstractUrl = doc.RootElement.GetProperty("AbstractURL").GetString();
            if (!string.IsNullOrWhiteSpace(abstractText))
                results.Add(new SearchResult(abstractText, abstractUrl ?? "", "DuckDuckGo"));

            // Related topics
            if (doc.RootElement.TryGetProperty("RelatedTopics", out var topics))
            {
                foreach (var topic in topics.EnumerateArray().Take(maxResults - results.Count))
                {
                    var text = topic.TryGetProperty("Text", out var t) ? t.GetString() : null;
                    var link = topic.TryGetProperty("FirstURL", out var u) ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(text))
                        results.Add(new SearchResult(text, link ?? "", "DuckDuckGo"));
                }
            }

            return results;
        }
        catch
        {
            return [];
        }
    }
}

public sealed record SearchResult(string Text, string Url, string Source);
