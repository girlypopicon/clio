using System.Globalization;
using System.Text;
using System.Text.Json;
using CLIo.Models;

namespace CLIo.Services;

/// <summary>
/// Second-pass LLM call that runs on every user message (and on long brain dumps, chunked).
/// It asks the model for nothing but structured JSON: every concrete piece of data in the text
/// as memory key/value pairs, plus any tasks and dated events. The caller applies the result.
/// </summary>
public sealed class ConcreteDataExtractor
{
    private const int ChunkChars = 6000;

    private readonly ILLMProvider llm;
    private readonly DevLogService devLog;

    public ConcreteDataExtractor(ILLMProvider llm, DevLogService devLog)
    {
        this.llm = llm;
        this.devLog = devLog;
    }

    public async Task<ExtractionResult> ExtractAsync(string text, DateTimeOffset now, IReadOnlyList<MemoryItem> existingMemory)
    {
        var memory = new List<ExtractedMemory>();
        var tasks = new List<ExtractedTask>();
        var events = new List<ExtractedEvent>();
        var devNotes = new List<string>();

        foreach (var chunk in Chunk(text))
        {
            var response = await CallAsync(chunk, now, existingMemory);
            Parse(response, memory, tasks, events, devNotes);
        }

        return new ExtractionResult(ResolveBatchKeyConflicts(memory), tasks, events, devNotes);
    }

    /// <summary>
    /// Within one reply a key must not be both a value and a parent, and must not repeat.
    /// A value on a key that also has children in the same batch moves to key:summary; for repeated
    /// keys the last value wins.
    /// </summary>
    private static List<ExtractedMemory> ResolveBatchKeyConflicts(List<ExtractedMemory> items)
    {
        var latest = new Dictionary<string, ExtractedMemory>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            latest[item.Key] = item;

        var keys = latest.Keys.ToList();
        var result = new List<ExtractedMemory>();
        foreach (var item in latest.Values)
        {
            var hasChildren = keys.Any(k => k.StartsWith(item.Key + ":", StringComparison.OrdinalIgnoreCase));
            result.Add(hasChildren ? item with { Key = item.Key + ":summary" } : item);
        }

        return result;
    }

    private async Task<string> CallAsync(string chunk, DateTimeOffset now, IReadOnlyList<MemoryItem> existingMemory)
    {
        var messages = new List<LlmMessage>
        {
            new("system", BuildSystemPrompt(now, existingMemory), now),
            new("user", chunk, now)
        };

        var sb = new StringBuilder();
        using var purpose = LlmCall.Use("extract");
        try
        {
            await foreach (var token in llm.ChatAsync(messages))
                sb.Append(token);
        }
        catch (Exception ex)
        {
            devLog.Log($"Extraction call failed: {ex.Message}", "extract");
        }

        return sb.ToString();
    }

    private static string BuildSystemPrompt(DateTimeOffset now, IReadOnlyList<MemoryItem> existingMemory)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a data-extraction function inside a personal organizer. You do not chat. Read the user's text and return ONLY a JSON object, no prose, no code fences.");
        sb.AppendLine("Schema: {\"memory\":[{\"key\":\"category:sub:item\",\"value\":\"...\"}],\"tasks\":[{\"title\":\"...\",\"due\":\"ISO 8601 with offset or empty\"}],\"events\":[{\"title\":\"...\",\"when\":\"ISO 8601 with offset\",\"details\":\"...\"}],\"dev_notes\":[\"...\"]}");
        sb.AppendLine("Extract EVERY concrete piece of data the user states about themselves, their people, projects, tools, accounts, places, and plans: names, nicknames, phone numbers, emails, addresses, URLs, usernames, birthdays and other dates, ages, amounts of money, quantities, versions, ids, model names, schedules, routines, relationships, employers, locations, preferences, dislikes, habits, goals, deadlines, statuses.");
        sb.AppendLine("One fact per entry. Keep values short and literal (copy numbers, spellings and units exactly). Split compound statements into separate keys.");
        sb.AppendLine("Memory keys are colon-delimited lowercase snake_case, max 5 segments, and the FIRST segment must be one of: user, projects, people, systems, tools, goals, knowledge, context, meta, flex.");
        sb.AppendLine("Examples: user:name, user:birthdate, user:location, user:occupation, people:sarah:relationship, people:sarah:phone, projects:clio:status, projects:clio:stack, tools:editor, systems:pc:os, goals:active, flex:preference:coffee_black, flex:pattern:night_owl.");
        sb.AppendLine("Facts about a named person go under people:<name>:<attribute>. The user's own facts go under user:. Preferences use flex:preference:<slug>, recurring patterns use flex:pattern:<slug>.");
        sb.AppendLine("If a fact updates an existing key listed below, REUSE that exact key so it overwrites instead of duplicating.");
        sb.AppendLine("KEY RULES: (1) One thing, one prefix. If a person, machine, project or place already appears in the memory list below, file new facts about it under that SAME prefix; never invent a second prefix for it. Names the user uses interchangeably (for example 'Big server' and 'XXL') are the same entity. (2) If the user says how something should be filed (for example 'use the key systems:webserver'), follow it exactly. (3) A key is either a value or a parent, never both: never put a child under a key that already holds a value, and never write a value on a key that already has children (use an attribute such as name, spoken_name, summary instead). (4) Aliases and spellings are attributes (...:name, ...:spoken_name, ...:written_name), not new prefixes.");
        sb.AppendLine("A task is something the user says they need or must do; set \"due\" only when the user ties it to a specific date or time, otherwise leave it empty. An event is a calendar reminder or appointment that is NOT something to do (a birthday, a meeting, a flight, a bill date). An event needs a concrete date or time; compute 'when' from the current time below, never copy relative words. Skip events with no resolvable date (put them in memory as context instead).");
        sb.AppendLine("dev_notes are feature requests, bug reports, or complaints about CLIo the app itself ('you should be able to...', 'it keeps doing X', 'that is broken'); one clear sentence each. They are NOT the user's personal to-dos and NOT memory.");
        sb.AppendLine("Skip: questions, small talk, hypotheticals, things the user is unsure about ('maybe', 'I think'), commands to the assistant, and anything about the assistant itself. If there is nothing concrete, return {\"memory\":[],\"tasks\":[],\"events\":[],\"dev_notes\":[]}.");
        sb.AppendLine("If a task or event has a day but no time, use 09:00 local time; use 00:00 only for birthdays and other all-day dates. Weekday names ('Sunday', 'next Friday') mean the NEXT such day in the calendar table below, never a past day.");
        sb.AppendLine($"Current local time: {now:O}");
        sb.AppendLine("Calendar for the next two weeks (use these exact dates):");
        for (var i = 0; i < 14; i++)
        {
            var day = now.AddDays(i);
            sb.AppendLine($"{day:dddd} = {day:yyyy-MM-dd}{(i == 0 ? " (today)" : string.Empty)}");
        }

        var populated = existingMemory.Where(m => !string.IsNullOrWhiteSpace(m.Value)).Take(200).ToList();
        if (populated.Count > 0)
        {
            var prefixes = populated
                .Select(m => string.Join(':', m.Key.Split(':').Take(3)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(prefix => prefix, StringComparer.OrdinalIgnoreCase)
                .Take(100);
            sb.AppendLine("Existing entity prefixes (reuse the matching one for the same thing): " + string.Join(", ", prefixes));
            sb.AppendLine("Existing memory keys:");
            foreach (var item in populated)
                sb.AppendLine($"{item.Key} = {item.Value}");
        }

        return sb.ToString();
    }

    private void Parse(string response, List<ExtractedMemory> memory, List<ExtractedTask> tasks, List<ExtractedEvent> events, List<string> devNotes)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            if (!string.IsNullOrWhiteSpace(response))
                devLog.Log("Extraction returned non-JSON; skipped.", "extract");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(response[start..(end + 1)]);
            var root = doc.RootElement;

            if (root.TryGetProperty("memory", out var mem) && mem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in mem.EnumerateArray())
                {
                    var key = Str(item, "key").Trim().ToLowerInvariant();
                    var value = Str(item, "value").Trim();
                    if (key.Length > 0 && value.Length > 0)
                        memory.Add(new ExtractedMemory(key, value));
                }
            }

            if (root.TryGetProperty("tasks", out var tk) && tk.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tk.EnumerateArray())
                {
                    var title = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : Str(item, "title");
                    if (string.IsNullOrWhiteSpace(title))
                        continue;

                    DateTimeOffset? due = DateTimeOffset.TryParse(Str(item, "due"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                    tasks.Add(new ExtractedTask(title.Trim(), due));
                }
            }

            if (root.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ev.EnumerateArray())
                {
                    var title = Str(item, "title").Trim();
                    if (title.Length > 0 && DateTimeOffset.TryParse(Str(item, "when"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
                        events.Add(new ExtractedEvent(title, when, Str(item, "details").Trim()));
                }
            }

            if (root.TryGetProperty("dev_notes", out var dn) && dn.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dn.EnumerateArray())
                {
                    var note = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : Str(item, "note");
                    if (!string.IsNullOrWhiteSpace(note))
                        devNotes.Add(note.Trim());
                }
            }
        }
        catch (JsonException ex)
        {
            devLog.Log($"Extraction JSON parse failed: {ex.Message}", "extract");
        }
    }

    private static string Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";

    private static IEnumerable<string> Chunk(string text)
    {
        if (text.Length <= ChunkChars)
        {
            yield return text;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var paragraph in text.Split('\n'))
        {
            if (current.Length + paragraph.Length + 1 > ChunkChars && current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }

            current.AppendLine(paragraph);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }
}

public sealed record ExtractedMemory(string Key, string Value);

public sealed record ExtractedTask(string Title, DateTimeOffset? Due);

public sealed record ExtractedEvent(string Title, DateTimeOffset When, string Details);

public sealed record ExtractionResult(
    IReadOnlyList<ExtractedMemory> Memory,
    IReadOnlyList<ExtractedTask> Tasks,
    IReadOnlyList<ExtractedEvent> Events,
    IReadOnlyList<string> DevNotes)
{
    public bool IsEmpty => Memory.Count == 0 && Tasks.Count == 0 && Events.Count == 0 && DevNotes.Count == 0;
}
