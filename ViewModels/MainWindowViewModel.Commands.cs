using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using CLIo.Models;
using CLIo.Services;
using CLIo.Services.Skills;
using CLIo.Services.Update;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CLIo.ViewModels;

public partial class MainWindowViewModel
{
    private async Task HandlePrefixCommandAsync(string prefix, string body, string rawInput, DateTimeOffset now)
    {
        var routedAgent = router.Route(prefix, body);
        devLog.Log($"Prefix '{prefix}' routed to {routedAgent}: {body[..Math.Min(body.Length, 60)]}", "routing");
        var userMessage = new ChatMessage { Role = "user", Content = rawInput, Timestamp = now };
        ChatMessages.Add(userMessage);
        await database.AddChatMessageAsync(SelectedThreadId, userMessage.Role, userMessage.Content, userMessage.Timestamp);

        if (prefix == "__")
        {
            await HandleDisplayCommandAsync(body, now);
            return;
        }

        if (prefix == "@@")
        {
            await HandleReminderCommandAsync(body, now);
            return;
        }

        var assistant = new ChatMessage { Role = "assistant", Content = string.Empty, Timestamp = DateTimeOffset.Now };
        ChatMessages.Add(assistant);

        await LoadEventsAsync();
        await LoadTasksAsync();

        var llmMessages = BuildPrefixLlmMessages(prefix, body, now);

        using (LlmCall.Use("command"))
        {
            await foreach (var token in llmProvider.ChatAsync(llmMessages))
            {
                assistant.Content += token;
            }
        }

        await LogLlmExchangeAsync(llmMessages, assistant.Content);
        await ApplyStructuredMemoryAsync(assistant, body);

        if (prefix == "--" && !string.IsNullOrWhiteSpace(body))
        {
            await TryDeleteTaskByPrefixAsync(assistant, body);
        }

        await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
        await LoadThreadsAsync();
    }

    private async Task TryDeleteTaskByPrefixAsync(ChatMessage assistant, string body)
    {
        if (assistant.ActionBadges.Count > 0)
        {
            return;
        }

        var normalized = body.Trim();
        var match = Tasks.FirstOrDefault(t =>
            !t.IsDone && t.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase));
        match ??= Tasks.FirstOrDefault(t =>
            !t.IsDone && normalized.Contains(t.Title, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            await taskService.DeleteAsync(match.Id);
            await LoadTasksAsync();
            assistant.Content = $"Removed task: {match.Title}";
            assistant.ActionBadges = new ObservableCollection<ActionBadge>(new[]
            {
                MakeBadge("×", $"-task: {match.Title}", "#4A2A2E")
            });
        }
    }

    private async Task HandleReminderCommandAsync(string body, DateTimeOffset now)
    {
        var parsed = dateTimeParser.ParseFirstWithText(body, now);
        string reply;

        if (parsed is null)
        {
            reply = "When should I remind you? Try: @@ call mom in 30 minutes, or @@ take meds tomorrow at 8am.";
        }
        else
        {
            var (when, matched) = parsed.Value;
            if (when <= now && now - when < TimeSpan.FromDays(1))
            {
                // A bare time like "5pm" that already passed today means tomorrow.
                when = when.AddDays(1);
            }

            var title = body.Replace(matched, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+(to|at|on|by|in|for|me)$|^(to|me to|remind me to)\s+", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim(' ', ',', '.');
            if (string.IsNullOrWhiteSpace(title))
            {
                title = body.Trim();
            }

            await reminderService.AddAsync(title, when);
            reply = $"Okay, I'll remind you: {title} ({when:ddd MMM d, h:mm tt}).";
        }

        var assistant = new ChatMessage { Role = "assistant", Content = reply, Timestamp = DateTimeOffset.Now };
        ChatMessages.Add(assistant);
        await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
    }

    private async Task HandleDisplayCommandAsync(string body, DateTimeOffset now)
    {
        var normalized = body.ToLowerInvariant().Trim();

        if (normalized is "reminders" or "reminder" or "remind")
        {
            var pending = await reminderService.GetPendingAsync();
            var text = pending.Count == 0
                ? "No pending reminders. Add one with: @@ call mom in 30 minutes"
                : "Pending reminders:\n" + string.Join("\n", pending.Select(r => $"#{r.Id} | {r.DisplayDueAt} | {r.Title}"))
                  + "\n\nCancel one with __unremind <id>.";
            var listing = new ChatMessage { Role = "assistant", Content = text, Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(listing);
            await database.AddChatMessageAsync(SelectedThreadId, listing.Role, listing.Content, listing.Timestamp);
            return;
        }

        if (normalized.StartsWith("unremind ") && int.TryParse(normalized[9..].Trim().TrimStart('#'), out var reminderId))
        {
            await reminderService.DismissAsync(reminderId);
            var cancelled = new ChatMessage { Role = "assistant", Content = $"Cancelled reminder #{reminderId}.", Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(cancelled);
            await database.AddChatMessageAsync(SelectedThreadId, cancelled.Role, cancelled.Content, cancelled.Timestamp);
            return;
        }

        if (normalized is "tasks" or "task" or "todo" or "to-do" or "todos" or "to-dos")
        {
            CurrentView = "Tasks";
            var assistant = new ChatMessage { Role = "assistant", Content = "Switched to Tasks.", Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(assistant);
            await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            return;
        }

        if (normalized is "memory" or "memories" || ValidCategories.Contains(normalized))
        {
            CurrentView = "Memory";
            if (ValidCategories.Contains(normalized))
            {
                SelectedMemorySection = normalized;
            }
            else
            {
                SelectedMemorySection = "user";
            }

            var assistant = new ChatMessage { Role = "assistant", Content = $"Switched to Memory ({SelectedMemorySection}).", Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(assistant);
            await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            return;
        }

        if (normalized.StartsWith("forget "))
        {
            var key = body[7..].Trim();
            await memoryService.DeleteAsync(key);
            await database.AddMemoryTransactionAsync("delete", key, string.Empty, "success", "manual_command");
            await LoadMemoryAsync();
            var assistant = new ChatMessage { Role = "assistant", Content = $"Forgot memory key: {key}", Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(assistant);
            await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            return;
        }

        if (normalized.StartsWith("rename "))
        {
            var parts = body[7..].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                await memoryService.RenameKeyAsync(parts[0], parts[1]);
                await database.AddMemoryTransactionAsync("rename", parts[0], parts[1], "success", "manual_command");
                await LoadMemoryAsync();
                var assistant = new ChatMessage { Role = "assistant", Content = $"Renamed {parts[0]} → {parts[1]}", Timestamp = DateTimeOffset.Now };
                ChatMessages.Add(assistant);
                await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            }
            else
            {
                var assistant = new ChatMessage { Role = "assistant", Content = "Usage: __rename old:key new:key", Timestamp = DateTimeOffset.Now };
                ChatMessages.Add(assistant);
                await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            }
            return;
        }

        if (normalized.StartsWith("merge "))
        {
            var parts = body[6..].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                await memoryService.MergeKeysAsync(parts[0], parts[1]);
                await database.AddMemoryTransactionAsync("merge", parts[0], parts[1], "success", "manual_command");
                await LoadMemoryAsync();
                var assistant = new ChatMessage { Role = "assistant", Content = $"Merged {parts[1]} into {parts[0]}", Timestamp = DateTimeOffset.Now };
                ChatMessages.Add(assistant);
                await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            }
            else
            {
                var assistant = new ChatMessage { Role = "assistant", Content = "Usage: __merge target:key source:key", Timestamp = DateTimeOffset.Now };
                ChatMessages.Add(assistant);
                await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            }
            return;
        }

        if (normalized.StartsWith("search "))
        {
            var term = body[7..].Trim();
            var results = await memoryService.SearchAsync(term);
            var content = results.Count == 0
                ? $"No memory items matching '{term}'."
                : $"Found {results.Count} match(es):\n" + string.Join("\n", results.Select(r => $"  {r.Key}: {r.Value}"));
            var assistant = new ChatMessage { Role = "assistant", Content = content, Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(assistant);
            await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            return;
        }

        if (normalized is "compact")
        {
            await HandleCompactCommandAsync(now);
            return;
        }

        if (normalized is "dev" or "devlog" or "log")
        {
            var content = devLog.Format();
            var assistant = new ChatMessage { Role = "assistant", Content = content, Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(assistant);
            await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            return;
        }

        if (normalized.StartsWith("note add ") || (normalized.StartsWith("note ") && !normalized.StartsWith("notes") && !normalized.StartsWith("note done ") && !normalized.StartsWith("note del ")))
        {
            var noteText = normalized.StartsWith("note add ") ? body[9..].Trim() : body[5..].Trim();
            if (!string.IsNullOrWhiteSpace(noteText))
            {
                var id = await database.AddDevNoteAsync(noteText, "user");
                devLog.Log($"Dev note #{id} added: {noteText}", "notes");
                var assistant = new ChatMessage { Role = "assistant", Content = $"Dev note #{id} added: {noteText}", Timestamp = DateTimeOffset.Now };
                ChatMessages.Add(assistant);
                await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            }
            return;
        }

        if (normalized.StartsWith("note done "))
        {
            if (int.TryParse(body[10..].Trim().TrimStart('#'), out var noteId))
            {
                await database.CompleteDevNoteAsync(noteId);
                devLog.Log($"Dev note #{noteId} marked done", "notes");
                var assistant = new ChatMessage { Role = "assistant", Content = $"Dev note #{noteId} marked done.", Timestamp = DateTimeOffset.Now };
                ChatMessages.Add(assistant);
                await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            }
            return;
        }

        if (normalized.StartsWith("note del "))
        {
            if (int.TryParse(body[9..].Trim().TrimStart('#'), out var noteId))
            {
                await database.DeleteDevNoteAsync(noteId);
                devLog.Log($"Dev note #{noteId} deleted", "notes");
                var assistant = new ChatMessage { Role = "assistant", Content = $"Dev note #{noteId} deleted.", Timestamp = DateTimeOffset.Now };
                ChatMessages.Add(assistant);
                await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            }
            return;
        }

        if (normalized is "notes" or "devnotes")
        {
            var notes = await database.GetDevNotesAsync();
            string content;
            if (notes.Count == 0)
            {
                content = "No dev notes yet. Use __note <idea> to add one.";
            }
            else
            {
                var open = notes.Where(n => n.Status == "open").ToList();
                var done = notes.Where(n => n.Status == "done").ToList();
                var sb = new StringBuilder();
                sb.AppendLine($"Dev notes ({open.Count} open, {done.Count} done):");
                foreach (var note in open)
                    sb.AppendLine($"  #{note.Id} [{note.Source}] {note.Note}");
                if (done.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Completed:");
                    foreach (var note in done.Take(10))
                        sb.AppendLine($"  #{note.Id} ✓ {note.Note}");
                }
                content = sb.ToString().TrimEnd();
            }
            var assistant = new ChatMessage { Role = "assistant", Content = content, Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(assistant);
            await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            return;
        }

        if (normalized is "update" or "updates" or "version")
        {
            var info = await updateService.CheckAsync(force: true);
            var listing = new ChatMessage { Role = "assistant", Content = UpdateService.Describe(info), Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(listing);
            await database.AddChatMessageAsync(SelectedThreadId, listing.Role, listing.Content, listing.Timestamp);
            return;
        }

        if (normalized == "repeat" || normalized.StartsWith("repeat "))
        {
            var repeatReply = new ChatMessage { Role = "assistant", Content = await HandleRepeatCommandAsync(body.Trim()[6..].Trim()), Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(repeatReply);
            await database.AddChatMessageAsync(SelectedThreadId, repeatReply.Role, repeatReply.Content, repeatReply.Timestamp);
            return;
        }

        if (normalized is "tidy")
        {
            var (found, added) = await RunTidyScanAsync();
            var tidyNote = new ChatMessage
            {
                Role = "assistant",
                Timestamp = DateTimeOffset.Now,
                Content = found == 0
                    ? "Tidy scan: nothing looks duplicated or badly shaped."
                    : $"Tidy scan: {found} thing(s) worth a look, {added} new. They are in Review (menu > Review); nothing changes until you decide."
            };
            ChatMessages.Add(tidyNote);
            await database.AddChatMessageAsync(SelectedThreadId, tidyNote.Role, tidyNote.Content, tidyNote.Timestamp);
            return;
        }

        if (normalized is "corrections")
        {
            var rows = await database.GetCorrectionsAsync(20);
            var report = new ChatMessage
            {
                Role = "assistant",
                Timestamp = DateTimeOffset.Now,
                Content = rows.Count == 0
                    ? "No corrections recorded yet. A correction is logged whenever you change or delete something by hand, or settle a Review item."
                    : "Your recent corrections (newest first):\n" + string.Join("\n", rows.Select(r =>
                        $"  {r.Time.ToLocalTime():MM-dd HH:mm} {r.Kind} {r.Target}: \"{Shorten(r.OldValue, 50)}\" -> \"{Shorten(r.NewValue, 50)}\""))
            };
            ChatMessages.Add(report);
            await database.AddChatMessageAsync(SelectedThreadId, report.Role, report.Content, report.Timestamp);
            return;
        }

        if (normalized is "events" || normalized.StartsWith("events "))
        {
            var filters = body.Length > 6 ? body[6..].Trim() : string.Empty;
            var report = new ChatMessage
            {
                Role = "assistant",
                Content = await QueryRuntimeLogSkill.BuildReportAsync(runtimeLog, filters),
                Timestamp = DateTimeOffset.Now
            };
            ChatMessages.Add(report);
            await database.AddChatMessageAsync(SelectedThreadId, report.Role, report.Content, report.Timestamp);
            return;
        }

        if (normalized is "skills" or "agents")
        {
            var sb = new StringBuilder();
            sb.AppendLine("Skills (CLIo uses these by itself when an answer needs them):");
            foreach (var skill in skillRegistry.Skills)
                sb.AppendLine($"  {skill.Name} - {skill.Summary} [{(skill.Permission == SkillPermission.ReadOnly ? "read-only" : "needs your approval")}]");
            sb.AppendLine();
            sb.AppendLine("Agents (start a message with @Name to use one for that reply):");
            if (agentCatalog.Agents.Count == 0)
                sb.AppendLine("  none found. Put .agent.md files in %LOCALAPPDATA%/CLIo/agents.");
            foreach (var agentFile in agentCatalog.Agents)
                sb.AppendLine($"  @{agentFile.Id} - {agentFile.Description}");

            var listing = new ChatMessage { Role = "assistant", Content = sb.ToString().TrimEnd(), Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(listing);
            await database.AddChatMessageAsync(SelectedThreadId, listing.Role, listing.Content, listing.Timestamp);
            return;
        }

        if (normalized.StartsWith("web ") || normalized.StartsWith("google ") || normalized.StartsWith("lookup "))
        {
            var query = body.Split(' ', 2).ElementAtOrDefault(1)?.Trim() ?? body.Trim();
            var results = await webSearch.SearchAsync(query);
            string content;
            if (results.Count == 0)
            {
                content = $"No results found for \"{query}\".";
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Search results for \"{query}\":");
                foreach (var result in results)
                {
                    sb.AppendLine($"  • {result.Text}");
                    if (!string.IsNullOrWhiteSpace(result.Url))
                        sb.AppendLine($"    {result.Url}");
                }
                content = sb.ToString().TrimEnd();
            }
            devLog.Log($"Web search: {query} ({results.Count} results)", "search");
            var assistant = new ChatMessage { Role = "assistant", Content = content, Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(assistant);
            await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            return;
        }

        if (normalized.StartsWith("schedule ") && normalized.Contains('#'))
        {
            var idStr = body.Split('#', 2).ElementAtOrDefault(1)?.Trim().Split(' ', 2).FirstOrDefault();
            if (int.TryParse(idStr, out var taskId))
            {
                var task = Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is not null)
                {
                    TaskBeingScheduled = task;
                    IsScheduleTaskModalOpen = true;
                    var assistant = new ChatMessage { Role = "assistant", Content = $"Opening scheduler for task #{taskId}: {task.Title}", Timestamp = DateTimeOffset.Now };
                    ChatMessages.Add(assistant);
                    await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
                }
                else
                {
                    var assistant = new ChatMessage { Role = "assistant", Content = $"Task #{taskId} not found.", Timestamp = DateTimeOffset.Now };
                    ChatMessages.Add(assistant);
                    await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
                }
            }
            return;
        }

        if (normalized is "dedupe" or "clean")
        {
            await HandleDedupeCommandAsync(now);
            return;
        }

        string mode;
        DateTime? anchor = null;

        if (normalized.Contains("month"))
        {
            mode = "Month";
            if (normalized.Contains("next"))
            {
                anchor = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
            }
        }
        else if (normalized.Contains("two") || normalized.Contains("2") || normalized.Contains("fortnight"))
        {
            mode = "TwoWeek";
            if (normalized.Contains("next"))
            {
                anchor = DateTime.Today.AddDays(14);
            }
        }
        else
        {
            mode = "Week";
            if (normalized.Contains("next"))
            {
                anchor = DateTime.Today.AddDays(7);
            }
        }

        await LoadEventsAsync();

        var directive = new CalendarDirective(mode, anchor);
        var calAssistant = new ChatMessage { Role = "assistant", Content = string.Empty, Timestamp = DateTimeOffset.Now };
        ChatMessages.Add(calAssistant);
        ApplyCalendarDirective(calAssistant, directive);
        await database.AddChatMessageAsync(SelectedThreadId, calAssistant.Role, calAssistant.Content, calAssistant.Timestamp);
        await LoadThreadsAsync();
    }

    private async Task HandleDedupeCommandAsync(DateTimeOffset now)
    {
        var allMemory = await memoryService.GetAllMemoryAsync();
        var allTasks = await taskService.GetAllAsync();
        var sb = new StringBuilder();
        var deduped = 0;

        // Find duplicate memory keys (same value)
        var memByValue = allMemory
            .Where(m => !string.IsNullOrWhiteSpace(m.Value))
            .GroupBy(m => m.Value.ToLowerInvariant().Trim())
            .Where(g => g.Count() > 1)
            .ToList();

        if (memByValue.Count > 0)
        {
            sb.AppendLine("Duplicate memory entries (same value):");
            foreach (var group in memByValue)
            {
                var keys = group.Select(m => m.Key).ToList();
                sb.AppendLine($"  {string.Join(", ", keys)}: \"{group.First().Value[..Math.Min(group.First().Value.Length, 50)]}\"");
            }
        }

        // Find duplicate tasks (same title, both open)
        var taskDupes = allTasks
            .Where(t => !t.IsDone)
            .GroupBy(t => t.Title.ToLowerInvariant().Trim())
            .Where(g => g.Count() > 1)
            .ToList();

        if (taskDupes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Duplicate open tasks:");
            foreach (var group in taskDupes)
            {
                var ids = group.Select(t => $"#{t.Id}").ToList();
                sb.AppendLine($"  \"{group.First().Title}\" — {string.Join(", ", ids)}");
                // Auto-remove all but the first
                foreach (var dupe in group.Skip(1))
                {
                    await taskService.DeleteAsync(dupe.Id);
                    deduped++;
                }
            }
            await LoadTasksAsync();
        }

        if (sb.Length == 0)
            sb.AppendLine("No duplicates found in tasks or memory.");
        else if (deduped > 0)
            sb.AppendLine($"\nRemoved {deduped} duplicate task(s). Use __merge for memory duplicates.");

        devLog.Log($"Dedupe ran: {deduped} task dupes removed", "system");
        var assistant = new ChatMessage { Role = "assistant", Content = sb.ToString().TrimEnd(), Timestamp = DateTimeOffset.Now };
        ChatMessages.Add(assistant);
        await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
    }

    private async Task HandleCompactCommandAsync(DateTimeOffset now)
    {
        var allItems = await memoryService.GetAllMemoryAsync();
        var nonEmpty = allItems.Where(i => !string.IsNullOrWhiteSpace(i.Value)).ToList();
        var staleThreshold = now.AddDays(-90);

        var stale = nonEmpty
            .Where(i => DateTimeOffset.TryParse(i.UpdatedAt, out var updated) && updated < staleThreshold)
            .ToList();

        var categoryCounts = nonEmpty
            .GroupBy(i => i.Key.Contains(':') ? i.Key.Split(':')[0] : "uncategorized")
            .OrderByDescending(g => g.Count())
            .Select(g => $"  {g.Key}: {g.Count()} keys")
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Memory compaction report ({nonEmpty.Count} populated keys):");
        sb.AppendLine();
        sb.AppendLine("By category:");
        foreach (var line in categoryCounts)
            sb.AppendLine(line);

        if (stale.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Stale keys (not updated in 90+ days): {stale.Count}");
            foreach (var item in stale.Take(20))
                sb.AppendLine($"  {item.Key}: {item.Value[..Math.Min(item.Value.Length, 60)]}");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("No stale keys found.");
        }

        sb.AppendLine();
        sb.AppendLine("Use __forget <key> to remove, __merge <target> <source> to combine, __rename <old> <new> to reorganize.");

        var assistant = new ChatMessage { Role = "assistant", Content = sb.ToString().TrimEnd(), Timestamp = DateTimeOffset.Now };
        ChatMessages.Add(assistant);
        await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
    }

    private IReadOnlyList<LlmMessage> BuildPrefixLlmMessages(string prefix, string body, DateTimeOffset now)
    {
        var context = new StringBuilder();
        context.AppendLine($"Current local time: {now:O}");
        context.AppendLine("Reply with one short sentence confirming the action. Then append the structured block described below. NEVER wrap in code fences.");

        switch (prefix)
        {
            case "++":
                context.AppendLine("The user wants to ADD something.");
                context.AppendLine("If the input contains a date, time, day of week, or scheduling context → it's a calendar event. Emit:");
                context.AppendLine("SCHEDULE CANDIDATES");
                context.AppendLine("title | ISO 8601 datetime with offset | details");
                context.AppendLine("If the input has no date/time → it's a task. Emit:");
                context.AppendLine("TASK CANDIDATES");
                context.AppendLine("title | reason");
                context.AppendLine("If there are multiple items, list each on its own line in a single block.");
                break;
            case "--":
                context.AppendLine("The user wants to REMOVE/DELETE something. Match their input against the items listed below.");
                context.AppendLine("For calendar events, emit:");
                context.AppendLine("CALENDAR DELETE");
                context.AppendLine("title | date (optional)");
                context.AppendLine("If ambiguous (multiple matches), ask which one they mean instead of guessing.");
                break;
            case "==":
                context.AppendLine("The user wants to MOVE/RESCHEDULE a calendar event. Match the event and parse the new date/time.");
                context.AppendLine("Emit:");
                context.AppendLine("CALENDAR MOVE");
                context.AppendLine("title | old datetime (optional) | new ISO 8601 datetime with offset");
                context.AppendLine("If ambiguous, ask which one they mean.");
                break;
        }

        var relevantEvents = CalendarEvents
            .Where(item => item.StartsAt >= now.AddDays(-1))
            .OrderBy(item => item.StartsAt)
            .Take(30)
            .ToList();

        if (relevantEvents.Count > 0)
        {
            context.AppendLine();
            context.AppendLine("Calendar events:");
            foreach (var item in relevantEvents)
            {
                context.AppendLine($"- #{item.Id} | {item.StartsAt:O} | {item.Title} | {item.Details}");
            }
        }

        if (prefix is "--" or "++" && Tasks.Count > 0)
        {
            context.AppendLine();
            context.AppendLine("Tasks:");
            foreach (var task in Tasks.Where(t => !t.IsDone).Take(30))
            {
                context.AppendLine($"- #{task.Id} | {task.Title}");
            }
        }

        return new List<LlmMessage>
        {
            new("system", context.ToString(), now),
            new("user", body, now)
        };
    }

    private const string RepeatUsage = "Make an event repeat: __repeat #<id> every tuesday | every 2 weeks | weekdays | monthly | daily until 2026-12-31 | off. The #id is shown next to the event in the calendar list.";

    private async Task<string> HandleRepeatCommandAsync(string args)
    {
        await LoadEventsAsync();
        var match = Regex.Match(args, @"^#?(\d+)\s+(.+)$");
        if (!match.Success)
        {
            return RepeatUsage;
        }

        var id = int.Parse(match.Groups[1].Value);
        var series = CalendarEvents.FirstOrDefault(e => e.Id == id);
        if (series is null)
        {
            return $"No calendar event #{id}. {RepeatUsage}";
        }

        var phrase = match.Groups[2].Value.Trim();
        if (phrase.ToLowerInvariant() is "off" or "stop" or "none" or "once")
        {
            await calendarService.SetRecurrenceAsync(id, null);
            await LoadEventsAsync();
            return $"\"{series.Title}\" no longer repeats.";
        }

        if (!RecurrenceRule.TryParseNatural(phrase, out var rule))
        {
            return $"I couldn't read \"{phrase}\" as a repeat pattern. {RepeatUsage}";
        }

        await calendarService.SetRecurrenceAsync(id, rule);
        await LoadEventsAsync();
        return $"\"{series.Title}\" now repeats {rule.Describe()}, starting from its first date. Changes to one occurrence apply to the whole series.";
    }
}
