using System.Text.RegularExpressions;
using CLIo.Models;

namespace CLIo.Services;

public class MemoryService : IMemoryService
{
    private readonly JournalDatabaseService _database;

    public static HashSet<string> ValidCategories { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "projects", "people", "systems", "tools", "goals", "knowledge", "context", "meta", "flex"
    };

    public static HashSet<string> ImmutableMemoryKeys { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "user:name",
        "user:birthdate"
    };

    public MemoryService(JournalDatabaseService database)
    {
        _database = database;
    }

    public Task<IReadOnlyList<MemoryItem>> GetAllMemoryAsync() => _database.GetMemoryAsync();

    public Task<IReadOnlyList<MemoryTransaction>> GetTransactionsAsync(int limit = 80) => _database.GetMemoryTransactionsAsync(limit);

    public Task UpsertAsync(string key, string value, string type, string source = "chat") => _database.UpsertMemoryAsync(key, value, type, source);

    public Task DeleteAsync(string key) => _database.DeleteMemoryAsync(key);

    public Task<IReadOnlyList<MemoryItem>> SearchAsync(string term) => _database.SearchMemoryAsync(term);

    public Task RenameKeyAsync(string oldKey, string newKey) => _database.RenameMemoryKeyAsync(oldKey, newKey);

    public Task MergeKeysAsync(string targetKey, string sourceKey) => _database.MergeMemoryKeysAsync(targetKey, sourceKey);

    public Task<int> GetNonEmptyCountAsync() => _database.GetNonEmptyMemoryCountAsync();

    public MemoryValidationResult ValidateCandidate(MemoryCandidate candidate, string? existingValue, bool isMainChat, bool isExplicitWrite)
    {
        if (!IsValidMemoryKeyName(candidate.Key))
        {
            return new MemoryValidationResult("failed", "Invalid key format. Use category:subcategory:item format.", ShouldWrite: false);
        }

        var colonIndex = candidate.Key.IndexOf(':');
        if (colonIndex <= 0)
        {
            return new MemoryValidationResult("failed", "Key must start with a category prefix (e.g. user:, people:, goals:).", ShouldWrite: false);
        }

        var category = candidate.Key[..colonIndex];
        if (!ValidCategories.Contains(category))
        {
            return new MemoryValidationResult("failed", $"Unknown category '{category}'. Valid: {string.Join(", ", ValidCategories.OrderBy(c => c))}.", ShouldWrite: false);
        }

        if (string.IsNullOrWhiteSpace(candidate.Value))
        {
            return new MemoryValidationResult("failed", "Value is empty.", ShouldWrite: false);
        }

        if (IsUncertainMemoryValue(candidate.Value))
        {
            return new MemoryValidationResult("failed", "Uncertain or aspirational wording is context, not confirmed memory.", ShouldWrite: false);
        }

        if (candidate.Value.Length > 500)
        {
            return new MemoryValidationResult("failed", "Value is over 500 characters and may be truncated.", ShouldWrite: false);
        }

        if (string.Equals(existingValue, candidate.Value, StringComparison.Ordinal))
        {
            return new MemoryValidationResult("success", "Already stored.", ShouldWrite: false);
        }

        if (!string.IsNullOrWhiteSpace(existingValue) && ImmutableMemoryKeys.Contains(candidate.Key) && !isExplicitWrite)
        {
            return new MemoryValidationResult("pending_review", $"Immutable field already set to '{existingValue}'. Confirm before updating.", ShouldWrite: false);
        }

        if (!string.IsNullOrWhiteSpace(existingValue) && !isExplicitWrite)
        {
            return new MemoryValidationResult("pending_review", $"Existing value is '{existingValue}'. Confirm before updating.", ShouldWrite: false);
        }

        if (!isMainChat)
        {
            return new MemoryValidationResult("pending_review", "Detected in a scoped chat. Confirm from Main Chat or Memory before saving.", ShouldWrite: false);
        }

        if (!isExplicitWrite)
        {
            return new MemoryValidationResult("pending_review", "Detected possible memory. Awaiting confirmation.", ShouldWrite: false);
        }

        return new MemoryValidationResult("pending_review", string.Empty, ShouldWrite: true);
    }

    public string? MapMemoryField(string normalized)
    {
        var mapped = normalized switch
        {
            "name" => "user:name",
            "preferred name" => "user:preferred_name",
            "location" or "current location" or "where i live" => "user:location",
            "timezone" or "time zone" => "user:timezone",
            "focus" or "current focus" => "goals:current_focus",
            "project" or "current project" => "projects:current",
            "goal" or "goals" => "goals:active",
            "communication style" or "tone" or "style" => "user:communication_style",
            "occupation" or "job" => "user:occupation",
            "employer" or "work" => "user:employer",
            "partner" or "spouse" => "people:partner",
            "family" => "people:family",
            "pets" or "pet" => "people:pets",
            "hobbies" or "interests" => "user:interests",
            _ => null
        };

        if (mapped is not null)
            return mapped;

        if (normalized.Contains(':'))
        {
            var category = normalized.Split(':')[0];
            return ValidCategories.Contains(category) ? normalized : null;
        }

        return null;
    }

    public bool IsValidMemoryKeyName(string key)
    {
        return Regex.IsMatch(key, @"^[a-z][a-z0-9_]{0,29}(:[a-z][a-z0-9_]{0,29}){0,4}$", RegexOptions.CultureInvariant)
            && !key.Contains("__", StringComparison.Ordinal)
            && !string.Equals(key, "key", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(key, "field", StringComparison.OrdinalIgnoreCase);
    }

    public string MemoryTypeForKey(string key)
    {
        var colonIndex = key.IndexOf(':');
        if (colonIndex > 0)
        {
            var category = key[..colonIndex];
            if (ValidCategories.Contains(category))
                return category;
        }

        return "flex";
    }

    public string BuildPreferenceKey(string value) => "flex:preference:" + Slugify(value);

    public string BuildPatternKey(string value) => "flex:pattern:" + Slugify(value);

    public IReadOnlyList<MemoryCandidate> ExtractLocalMemoryCandidates(string text)
    {
        var candidates = new List<MemoryCandidate>();
        AddNaturalUpdateMatches(candidates, text);
        AddExplicitKeyValueMatches(candidates, text);
        AddMatch(candidates, text, "name", @"\b(?:actually\s+)?my name is\s+([a-zA-Z][a-zA-Z'-]{1,40})\b");
        AddMatch(candidates, text, "preferred_name", @"\b(?:call me|you can call me)\s+([A-Z][a-zA-Z'-]{1,40})\b");
        AddMatch(candidates, text, "current_location", @"\b(?:actually\s+)?(?:i live in|i'm in|i am in|i moved to|my location is|my location is now)\s+([^\.!,;\n]{2,80})");
        AddMatch(candidates, text, "timezone", @"\b(?:my timezone is|i'm in the|i am in the)\s+([^\.!,;\n]{2,80}\s+timezone|[A-Z]{2,4})");
        AddMatch(candidates, text, "current_focus", @"\b(?:actually\s+)?(?:my current focus is|my focus is now|right now i'm focused on|right now i am focused on|i'm focused on|i am focused on)\s+([^\.\n]{3,160})");
        AddMatch(candidates, text, "current_project", @"\b(?:actually\s+)?(?:my current project is|my project is now|i'm working on|i am working on|the project is)\s+([^\.\n]{3,160})");
        AddMatch(candidates, text, "goals", @"\b(?:actually\s+)?(?:my goal is|my goal is now|i want to|i'm trying to|i am trying to)\s+([^\.\n]{3,180})");
        AddPreferenceMatch(candidates, text, @"\b(?:actually\s+)?(?:i prefer|my preference is|my preference is now|i like it when|i like when|please)\s+([^\.\n]{3,180})");
        AddMatch(candidates, text, "communication_style", @"\b(?:talk to me|respond to me|write to me)\s+([^\.\n]{3,180})");

        return candidates;
    }

    private void AddExplicitKeyValueMatches(List<MemoryCandidate> candidates, string text)
    {
        foreach (Match match in Regex.Matches(
            text,
            @"^\s*(?<field>[a-z][a-z_ ]{1,60})\s*:(?!//)\s*(?:""(?<quoted>[^""\n]{1,220})""|(?<plain>[^\n]{1,220}))\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline))
        {
            var value = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["plain"].Value;

            AddMappedCandidate(candidates, match.Groups["field"].Value, value, isExplicitWrite: false);
        }
    }

    private void AddNaturalUpdateMatches(List<MemoryCandidate> candidates, string text)
    {
        foreach (Match match in Regex.Matches(
            text,
            @"\b(?:change|update|set|remember)\s+(?:my\s+)?(?<field>[a-z_ ]{2,40})\s+(?:to|as|is)\s+(?<value>[^\.\n]{1,220})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            AddMappedCandidate(candidates, match.Groups["field"].Value, match.Groups["value"].Value, isExplicitWrite: true);
        }

        foreach (Match match in Regex.Matches(
            text,
            @"\b(?:my\s+)?(?<field>name|preferred name|location|current location|timezone|focus|current focus|project|current project|goal|preference|communication style)\s+(?:is now|changed to)\s+(?<value>[^\.\n]{1,220})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            AddMappedCandidate(candidates, match.Groups["field"].Value, match.Groups["value"].Value, isExplicitWrite: false);
        }
    }

    private void AddMatch(List<MemoryCandidate> candidates, string text, string key, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return;
        }

        var value = CleanMemoryValue(match.Groups[1].Value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        AddCandidate(candidates, key, value, isExplicitWrite: false);
    }

    private void AddPreferenceMatch(List<MemoryCandidate> candidates, string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return;
        }

        var value = CleanMemoryValue(match.Groups[1].Value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        AddCandidate(candidates, BuildPreferenceKey(value), value, isExplicitWrite: false);
    }

    private void AddMappedCandidate(List<MemoryCandidate> candidates, string field, string value, bool isExplicitWrite)
    {
        value = CleanMemoryValue(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = field.Trim().ToLowerInvariant().Replace("_", " ");
        if (normalized is "preference" or "preferences")
        {
            AddCandidate(candidates, BuildPreferenceKey(value), value, isExplicitWrite);
            return;
        }

        if (normalized is "pattern" or "patterns" or "routine" or "routines" or "tendency" or "tendencies")
        {
            AddCandidate(candidates, BuildPatternKey(value), value, isExplicitWrite);
            return;
        }

        var key = MapMemoryField(normalized);
        if (key is null)
        {
            return;
        }

        AddCandidate(candidates, key, value, isExplicitWrite);
    }

    private void AddCandidate(List<MemoryCandidate> candidates, string key, string value, bool isExplicitWrite)
    {
        if (!isExplicitWrite && candidates.Any(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase) && candidate.IsExplicitWrite))
        {
            return;
        }

        candidates.RemoveAll(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        candidates.Add(new MemoryCandidate(key, value, isExplicitWrite, MemoryTypeForKey(key)));
    }

    private static string CleanMemoryValue(string value)
    {
        return value.Trim().Trim('"', '\'', ' ', '.', ',', ';', ':', '!', '?');
    }

    private static bool IsUncertainMemoryValue(string value)
    {
        return Regex.IsMatch(
            value,
            @"\b(should be|maybe|probably|i think|i guess|might be|could be|hopefully|planning to|want to|hoping to)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Slugify(string value)
    {
        var slug = Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        if (slug.Length > 40)
        {
            slug = slug[..40].Trim('_');
        }

        return string.IsNullOrEmpty(slug) ? "item" : slug;
    }
}
