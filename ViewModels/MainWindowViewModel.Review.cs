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
    private async Task LoadReviewAsync()
    {
        var items = await database.GetOpenReviewItemsAsync();
        ReviewItems = new ObservableCollection<ReviewItem>(items);
        OpenReviewCount = items.Count;
    }

    [RelayCommand]
    private async Task ShowReviewAsync()
    {
        CurrentView = "Review";
        await LoadReviewAsync();
    }

    /// <summary>Use the chat's value: the user confirmed it, so it becomes a manual value.</summary>
    [RelayCommand]
    private async Task AcceptReviewAsync(ReviewItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsTidy)
        {
            await ApplyTidyAsync(item);
        }
        else if (item.Kind == "memory")
        {
            var type = memoryService.MemoryTypeForKey(item.TargetKey);
            await memoryService.UpsertAsync(item.TargetKey, item.ProposedValue, type, "manual");
            await database.AddMemoryTransactionAsync("write", item.TargetKey, item.ProposedValue, "success", "review:accepted", type: type);
            await LoadMemoryAsync();
            await LoadMemoryTransactionsAsync();
        }
        else if (item.Kind == "event"
                 && int.TryParse(item.TargetKey, out var eventId)
                 && DateTimeOffset.TryParse(item.ProposedValue, out var newStart))
        {
            var existing = CalendarEvents.FirstOrDefault(e => e.Id == eventId);
            if (existing is not null)
            {
                await calendarService.UpdateEventAsync(existing.Id, existing.Title, newStart, existing.Details, "manual");
                await LoadEventsAsync();
            }
        }

        await database.ResolveReviewItemAsync(item.Id, "accepted");
        NoteCorrection(item.IsTidy ? "tidy_accept" : "review_accept", item.TargetKey, item.CurrentValue, item.ProposedValue, item.Label);
        runtimeLog.Record("review", "accepted", "ok", $"{item.Kind}: {item.Label}");
        await LoadReviewAsync();
    }

    /// <summary>Keep the user's value and remember not to propose this same claim again.</summary>
    [RelayCommand]
    private async Task DismissReviewAsync(ReviewItem? item)
    {
        if (item is null)
        {
            return;
        }

        await database.ResolveReviewItemAsync(item.Id, "dismissed");

        // "Keep mine" rejects what chat claimed: exactly the signal that shows where filing was wrong.
        NoteCorrection(item.IsTidy ? "tidy_decline" : "review_keep", item.TargetKey, item.IsTidy ? item.ProposedValue : item.ProposedValue, item.CurrentValue, item.Label);
        runtimeLog.Record("review", "dismissed", "ok", $"{item.Kind}: {item.Label}");
        await LoadReviewAsync();
    }

    // ---- approval for skills that act in the world ---------------------------------------------

    [ObservableProperty]
    private bool isApprovalOpen;

    [ObservableProperty]
    private string approvalTitle = string.Empty;

    [ObservableProperty]
    private string approvalPreview = string.Empty;

    private TaskCompletionSource<bool>? approvalSource;

    public Task<bool> RequestAsync(string title, string preview, CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => source.TrySetResult(false));
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            approvalSource?.TrySetResult(false);
            approvalSource = source;
            ApprovalTitle = title switch
            {
                "install_update" => "Install the update?",
                _ => $"Allow CLIo to run {title}?"
            };
            ApprovalPreview = preview;
            IsApprovalOpen = true;
        });
        return source.Task;
    }

    [RelayCommand]
    private void ApproveAction()
    {
        IsApprovalOpen = false;
        approvalSource?.TrySetResult(true);
    }

    [RelayCommand]
    private void DeclineAction()
    {
        IsApprovalOpen = false;
        approvalSource?.TrySetResult(false);
    }

    // ---- nightly tidy: finds duplicates and badly shaped entries, proposes, never decides ---------

    private void NoteCorrection(string kind, string target, string oldValue, string newValue, string note = "") =>
        _ = RecordCorrectionAsync(kind, target, oldValue, newValue, note);

    private async Task RecordCorrectionAsync(string kind, string target, string oldValue, string newValue, string note)
    {
        try
        {
            await database.AddCorrectionAsync(kind, target, oldValue, newValue, "manual", note);
            runtimeLog.Record("corrections", kind, "ok", $"{target}: \"{Shorten(oldValue, 60)}\" -> \"{Shorten(newValue, 60)}\"");
        }
        catch (Exception ex)
        {
            devLog.Log($"Could not record a correction: {ex.Message}", "corrections");
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>Runs the rule-based scan and puts anything worth a look into Review. Returns how many were new.</summary>
    private async Task<(int Found, int New)> RunTidyScanAsync()
    {
        var timer = runtimeLog.Start("tidy", "scan");
        try
        {
            await LoadMemoryAsync();
            await LoadEventsAsync();
            await LoadTasksAsync();

            var proposals = TidyScanner.Scan(MemoryItems.ToList(), CalendarEvents.ToList(), Tasks.ToList());
            var openBefore = (await database.GetOpenReviewItemsAsync()).Count;
            foreach (var p in proposals)
            {
                await database.AddReviewItemAsync(p.Kind, p.Target, p.Label, p.Current, p.Proposed, "tidy");
            }

            // a tidy suggestion that no longer applies (fixed by hand since) should not linger
            var current = proposals.Select(p => $"{p.Kind}|{p.Target}|{p.Proposed}").ToHashSet();
            foreach (var open in await database.GetOpenReviewItemsAsync())
            {
                if (open.IsTidy && !current.Contains($"{open.Kind}|{open.TargetKey}|{open.ProposedValue}"))
                {
                    await database.ResolveReviewItemAsync(open.Id, "stale");
                }
            }

            await LoadReviewAsync();
            var added = Math.Max(0, OpenReviewCount - openBefore);
            await database.SetSettingAsync("tidy_last_run", DateTimeOffset.Now.ToString("O"));
            timer.Done("ok", $"{proposals.Count} candidate(s), {added} new in Review");
            return (proposals.Count, added);
        }
        catch (Exception ex)
        {
            timer.Done("error", ex.Message);
            devLog.Log($"Tidy scan failed: {ex.Message}", "tidy");
            return (0, 0);
        }
    }

    /// <summary>About once a night while the app is open (or at the next start after 20 hours); never right at startup.</summary>
    private async Task RunTidyLoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(90));
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var last = DateTimeOffset.TryParse(await database.GetSettingAsync("tidy_last_run"), out var parsed) ? parsed : DateTimeOffset.MinValue;
                if (DateTimeOffset.Now - last > TimeSpan.FromHours(20))
                {
                    await RunTidyScanAsync();
                }
            }
            catch (Exception ex)
            {
                devLog.Log($"Tidy loop error: {ex.Message}", "tidy");
            }
        }
        while (await timer.WaitForNextTickAsync());
    }

    /// <summary>Carries out an accepted tidy proposal (see TidyApplier) and refreshes whatever it touched.</summary>
    private async Task ApplyTidyAsync(ReviewItem item)
    {
        var applied = await new TidyApplier(database, memoryService, calendarService, taskService).ApplyAsync(item);
        runtimeLog.Record("tidy", item.Kind, applied ? "ok" : "skipped", applied ? item.Label : "no longer applied, nothing changed: " + item.Label);

        await LoadMemoryAsync();
        await LoadMemoryTransactionsAsync();
        await LoadEventsAsync();
        await LoadTasksAsync();
    }

    // ---- update notice ------------------------------------------------------------------------

    [ObservableProperty]
    private bool isUpdateBannerVisible;

    [ObservableProperty]
    private string updateBannerText = string.Empty;

    [ObservableProperty]
    private bool canInstallUpdate;

    /// <summary>The banner's Install button asks for the same approved install the chat skill does.</summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        CanInstallUpdate = false;
        var text = await skillRegistry.RunAsync([new SkillCall("install_update", "latest")]);
        CanInstallUpdate = updateInstaller.GetLayout().CanSelfUpdate;
        var note = new ChatMessage
        {
            Role = "assistant",
            Content = text.Contains("declined", StringComparison.OrdinalIgnoreCase)
                ? "Okay, I won't install the update. Nothing was changed."
                : text.Replace("SKILL RESULTS. Use them to answer the user's last message.", string.Empty).Replace("<<<DATA", string.Empty).Replace("DATA>>>", string.Empty).Trim(),
            Timestamp = DateTimeOffset.Now
        };
        ChatMessages.Add(note);
        await database.AddChatMessageAsync(SelectedThreadId, note.Role, note.Content, note.Timestamp);
    }

    private string updateUrl = string.Empty;
    private string updateTag = string.Empty;

    [ObservableProperty]
    private bool isSourceUpdateBanner;

    [ObservableProperty]
    private bool hasReleasePage;

    /// <summary>The banner's Update button for a copy run from source: the same approved update the chat skill does.</summary>
    [RelayCommand]
    private async Task UpdateFromSourceAsync()
    {
        var text = await skillRegistry.RunAsync([new SkillCall("update_from_source", "latest")]);
        var declined = text.Contains("declined", StringComparison.OrdinalIgnoreCase);
        var note = new ChatMessage
        {
            Role = "assistant",
            Content = declined
                ? "Okay, I won't update. Nothing was changed."
                : text.Replace("SKILL RESULTS. Use them to answer the user's last message.", string.Empty).Replace("<<<DATA", string.Empty).Replace("DATA>>>", string.Empty).Trim(),
            Timestamp = DateTimeOffset.Now
        };
        ChatMessages.Add(note);
        await database.AddChatMessageAsync(SelectedThreadId, note.Role, note.Content, note.Timestamp);

        // whatever happened, look again so the banner is honest (a successful pull means "restart me")
        await RefreshSourceUpdateStateAsync();
        await ShowUpdateBannerFromSettingsAsync();
    }

    /// <summary>Looks at GitHub for new changes when this copy runs from source. Cheap, so it runs at every check.</summary>
    private async Task RefreshSourceUpdateStateAsync()
    {
        var status = await sourceUpdater.CheckAsync();
        if (!status.IsSourceCopy || status.Remote.Length == 0)
        {
            await database.SetSettingAsync("update_source_behind", "0");
            return;
        }

        await database.SetSettingAsync("update_source_behind", status.Behind.ToString());
        await database.SetSettingAsync("update_source_head", status.Head);
    }

    /// <summary>Checks the releases about once a day, and a source copy's GitHub changes at every tick (every 6 h and at start).</summary>
    private async Task RunUpdateLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                var last = DateTimeOffset.TryParse(await database.GetSettingAsync("update_last_check"), out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
                if (DateTimeOffset.Now - last > TimeSpan.FromHours(20))
                {
                    var info = await updateService.CheckAsync();
                    if (info.Ok)
                    {
                        await database.SetSettingAsync("update_last_check", DateTimeOffset.Now.ToString("O"));
                        await database.SetSettingAsync("update_latest_tag", info.IsNewer ? info.Tag : string.Empty);
                        await database.SetSettingAsync("update_latest_url", info.IsNewer ? info.Url : string.Empty);
                    }
                }

                await RefreshSourceUpdateStateAsync();
                await ShowUpdateBannerFromSettingsAsync();
            }
            catch (Exception ex)
            {
                devLog.Log($"Update check failed: {ex.Message}", "update");
            }
        }
        while (await timer.WaitForNextTickAsync());
    }

    private async Task ShowUpdateBannerFromSettingsAsync()
    {
        var tag = await database.GetSettingAsync("update_latest_tag");
        var url = await database.GetSettingAsync("update_latest_url");
        var dismissed = await database.GetSettingAsync("update_dismissed_tag");
        var sourceBehind = int.TryParse(await database.GetSettingAsync("update_source_behind"), out var behind) ? behind : 0;
        var sourceHead = await database.GetSettingAsync("update_source_head");
        var sourceKey = $"source:{sourceBehind}@{sourceHead}";

        // An installed release copy: a newer published release that the user has not dismissed.
        var isNewer = tag.Length > 0
            && Version.TryParse(tag.TrimStart('v', 'V'), out var latest)
            && AppVersion.Normalize(latest) > AppVersion.Parsed;
        var isSourceCopy = sourceUpdater.FindRoot() is not null;

        if (isNewer && !isSourceCopy && tag != dismissed && UpdateService.IsRepoUrl(url))
        {
            updateTag = tag;
            updateUrl = url;
            IsSourceUpdateBanner = false;
            HasReleasePage = true;
            UpdateBannerText = $"CLIo {tag.TrimStart('v', 'V')} is available. You're on {AppVersion.Text}.";
            CanInstallUpdate = updateInstaller.GetLayout().CanSelfUpdate;
            IsUpdateBannerVisible = true;
        }
        else if (isSourceCopy && sourceBehind > 0 && sourceKey != dismissed)
        {
            updateTag = sourceKey;
            updateUrl = string.Empty;
            IsSourceUpdateBanner = true;
            HasReleasePage = false;
            CanInstallUpdate = false;
            UpdateBannerText = $"{sourceBehind} new change{(sourceBehind == 1 ? string.Empty : "s")} on GitHub. You're on {sourceHead}.";
            IsUpdateBannerVisible = true;
        }
        else
        {
            IsUpdateBannerVisible = false;
        }
    }

    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (!UpdateService.IsRepoUrl(updateUrl))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(updateUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            devLog.Log($"Could not open the release page: {ex.Message}", "update");
        }
    }

    [RelayCommand]
    private async Task DismissUpdateAsync()
    {
        await database.SetSettingAsync("update_dismissed_tag", updateTag);
        IsUpdateBannerVisible = false;
    }
}
