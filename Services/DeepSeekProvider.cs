using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CLIo.Models;

namespace CLIo.Services;

public sealed class DeepSeekProvider : ILLMProvider
{
    private static readonly Uri Endpoint = new("https://api.deepseek.com/chat/completions");
    private readonly HttpClient httpClient = new();

    public async IAsyncEnumerable<string> ChatAsync(
        IEnumerable<LlmMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return "Add your DeepSeek API key in Settings to enable chat.";
            yield break;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var payload = new
        {
            model = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-chat",
            stream = true,
            stream_options = new { include_usage = true },
            messages = messages.Select(message => new
            {
                role = message.Role,
                content = message.Content
            })
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            yield return $"DeepSeek request failed: {(int)response.StatusCode} {response.ReasonPhrase}";
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[6..];
            if (data == "[DONE]")
            {
                yield break;
            }

            ReadUsage(data);

            var token = ReadToken(data);
            if (!string.IsNullOrEmpty(token))
            {
                yield return token;
            }
        }
    }

    /// <summary>The last streamed chunk carries token counts; hand them to whoever is recording this call.</summary>
    private static void ReadUsage(string json)
    {
        var stats = LlmCall.Stats.Value;
        if (stats is null || !json.Contains("\"usage\"", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                stats.PromptTokens = usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pi) ? pi : stats.PromptTokens;
                stats.CompletionTokens = usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var ci) ? ci : stats.CompletionTokens;
                stats.CacheHitTokens = usage.TryGetProperty("prompt_cache_hit_tokens", out var h) && h.TryGetInt32(out var hi) ? hi : stats.CacheHitTokens;
            }
        }
        catch (JsonException)
        {
            // usage is a bonus; never break the stream over it
        }
    }

    private static string? ReadToken(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta) || !delta.TryGetProperty("content", out var content))
        {
            return null;
        }

        return content.GetString();
    }
}
