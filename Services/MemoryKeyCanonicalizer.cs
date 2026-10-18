using System.Text.Json;

namespace CLIo.Services;

/// <summary>
/// Keeps automatic memory keys tidy before they are written:
///  1. Aliases: one entity, one prefix. e.g. {"systems:xxl": "systems:webserver:xxl"} sends
///     systems:xxl:cores to systems:webserver:xxl:cores.
///  2. A key may not be both a value and a parent. A child under an existing leaf becomes a sibling
///     (systems:webserver:xxl_cores), and a value on an existing parent becomes parent:summary.
/// </summary>
public sealed class MemoryKeyCanonicalizer
{
    private List<(string From, string To)> aliases = [];

    public void LoadAliases(string? json)
    {
        aliases = [];
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (map is not null)
            {
                aliases = map
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    .Select(pair => (pair.Key.Trim().ToLowerInvariant(), pair.Value.Trim().ToLowerInvariant()))
                    .OrderByDescending(pair => pair.Item1.Length)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // Bad alias JSON must never block memory writes.
        }
    }

    public string Canonicalize(string key, IReadOnlyCollection<string> existingKeys)
    {
        key = ApplyAlias(key.Trim().ToLowerInvariant());

        var existing = new HashSet<string>(existingKeys, StringComparer.OrdinalIgnoreCase);

        // A proper ancestor that already holds a value: this key cannot nest under it.
        var parts = key.Split(':');
        for (var length = 2; length < parts.Length; length++)
        {
            var ancestor = string.Join(':', parts.Take(length));
            if (existing.Contains(ancestor))
            {
                var tail = string.Join('_', parts.Skip(length));
                return ancestor + "_" + tail;
            }
        }

        // This key holds a value but already has children: keep the value as parent:summary.
        if (!existing.Contains(key) && existing.Any(k => k.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)))
        {
            return key + ":summary";
        }

        return key;
    }

    private string ApplyAlias(string key)
    {
        foreach (var (from, to) in aliases)
        {
            if (key.Equals(from, StringComparison.Ordinal))
            {
                return to;
            }

            if (key.StartsWith(from + ":", StringComparison.Ordinal))
            {
                return to + key[from.Length..];
            }
        }

        return key;
    }
}
