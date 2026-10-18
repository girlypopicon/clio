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

public partial class MainWindowViewModel : ViewModelBase, IApprovalProvider
{
    private static readonly HashSet<string> ValidCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "projects", "people", "systems", "tools", "goals", "knowledge", "context", "meta", "flex"
    };

    private static readonly HashSet<string> ImmutableMemoryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "user:name",
        "user:birthdate"
    };

    private readonly JournalDatabaseService database;
    private readonly ILLMProvider llmProvider;
    private readonly DataProtectionService dataProtection;
    private readonly IMemoryService memoryService;
    private readonly ICalendarService calendarService;
    private readonly ITaskService taskService;
    private readonly SystemPromptBuilder promptBuilder;
    private readonly Agents.ClioRouter router;
    private readonly ClioStateService clioState;
    private readonly DevLogService devLog;
    private readonly WebSearchService webSearch;

    public MainWindowViewModel(
        JournalDatabaseService database,
        ILLMProvider llmProvider,
        DataProtectionService dataProtection,
        IMemoryService memoryService,
        ICalendarService calendarService,
        ITaskService taskService,
        SystemPromptBuilder promptBuilder,
        Agents.ClioRouter router,
        ClioStateService clioState,
        DevLogService devLog,
        WebSearchService webSearch,
        ReminderService reminderService,
        DateTimeParsingService dateTimeParser,
        ISpeechToTextService speechToText,
        ITextToSpeechService textToSpeech,
        VoiceModelDownloader voiceDownloader,
        SkillRegistry skillRegistry,
        AgentCatalog agentCatalog,
        UpdateService updateService,
        UpdateInstaller updateInstaller,
        RuntimeLog runtimeLog,
        SourceUpdater sourceUpdater)
    {
        this.sourceUpdater = sourceUpdater;
        this.runtimeLog = runtimeLog;
        this.updateService = updateService;
        this.updateInstaller = updateInstaller;
        skillRegistry.Approvals = this;
        updateInstaller.Progress += text => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateBannerText = text;
            IsUpdateBannerVisible = true;
        });
        this.skillRegistry = skillRegistry;
        this.agentCatalog = agentCatalog;
        this.voiceDownloader = voiceDownloader;
        RefreshVoiceModelStatus();
        this.speechToText = speechToText;
        this.textToSpeech = textToSpeech;
        speechToText.PhraseRecognized += OnPhraseRecognized;
        speechToText.PartialRecognized += OnPartialRecognized;
        speechToText.Failed += message => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsListening = false;
            VoiceStatus = $"Mic problem: {message}";
            devLog.Log($"Speech failed: {message}", "voice");
        });
        speechToText.StatusChanged += status => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (status != "\u200b")
            {
                VoiceStatus = status;
            }
            else if (VoiceStatus == "Transcribing..." || VoiceStatus == "Loading Whisper model...")
            {
                VoiceStatus = string.Empty;
            }
        });
        speechToText.Diagnostic += message => devLog.Log(message, "voice");
        textToSpeech.SpeakingChanged += speaking => Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSpeaking = speaking);
        this.reminderService = reminderService;
        this.dateTimeParser = dateTimeParser;
        this.database = database;
        this.llmProvider = llmProvider;
        this.dataProtection = dataProtection;
        this.memoryService = memoryService;
        this.calendarService = calendarService;
        this.taskService = taskService;
        this.promptBuilder = promptBuilder;
        this.router = router;
        this.clioState = clioState;
        this.devLog = devLog;
        this.webSearch = webSearch;
        Initialization = LoadAsync();
    }
    private const string DefaultSttServerUrl = "https://clio-stt.stellas-cloudflare.workers.dev";
    private readonly ReminderService reminderService;
    private readonly VoiceModelDownloader voiceDownloader;
    private CancellationTokenSource? voiceDownloadCts;

    [ObservableProperty]
    private string voiceModelsStatus = string.Empty;

    [ObservableProperty]
    private string voiceDownloadProgress = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadVoiceModels))]
    private bool isDownloadingVoice;

    public bool CanDownloadVoiceModels => !IsDownloadingVoice && !voiceDownloader.AllInstalled;

    private readonly DateTimeParsingService dateTimeParser;
    private HashSet<string>? pendingMemoryConfirmationKeys;
    private List<MemoryCandidate>? pendingMemoryCandidates;

    [ObservableProperty]
    private string primaryInstructions = "You are CLIo, a calm, direct main chat companion and temporal organizer. The user can talk naturally, casually, and messily; your job is to help them think, notice durable facts, detect schedule pressure, identify open loops, and turn loose conversation into organized memory, calendar items, tasks, and summaries. Do not treat unconfirmed guesses as stored truth. Avoid dramatic reframing, therapy-speak, influencer phrasing, and generic validation filler.";

    [ObservableProperty]
    private string scratchpad = string.Empty;

    [ObservableProperty]
    private bool memoryToastVisible;

    [ObservableProperty]
    private string memoryToastMessage = string.Empty;

    [ObservableProperty]
    private bool isSettingsOpen;

    [ObservableProperty]
    private bool isThreadsPanelOpen = true;

    [ObservableProperty]
    private bool isToolsPanelOpen = true;

    [ObservableProperty]
    private string currentView = "Chat";

    [ObservableProperty]
    private string deepSeekApiKey = string.Empty;

    [ObservableProperty]
    private string settingsStatus = string.Empty;

    [ObservableProperty]
    private ObservableCollection<CalendarEvent> calendarEvents = [];

    [ObservableProperty]
    private ObservableCollection<CalendarEvent> upcomingCalendarEvents = [];

    [ObservableProperty]
    private CalendarEvent? selectedUpcomingCalendarEvent;

    [ObservableProperty]
    private string eventTitle = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? eventDate = DateTimeOffset.Now.Date;

    [ObservableProperty]
    private TimeSpan? eventTime;

    [ObservableProperty]
    private string eventDetails = string.Empty;

    [ObservableProperty]
    private DateTime calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty]
    private DateTime selectedCalendarDate = DateTime.Today;

    [ObservableProperty]
    private ObservableCollection<CalendarDayCell> calendarDays = [];

    [ObservableProperty]
    private ObservableCollection<CalendarEvent> selectedDayEvents = [];

    [ObservableProperty]
    private string calendarViewMode = "Month";

    public string CalendarMonthLabel => CalendarMonth.ToString("MMMM yyyy");

    public string SelectedCalendarDateLabel => SelectedCalendarDate.ToString("dddd, MMMM d, yyyy");

    public bool IsCalendarWeekView => CalendarViewMode == "Week";

    public bool IsCalendarTwoWeekView => CalendarViewMode == "TwoWeek";

    public bool IsCalendarMonthView => CalendarViewMode == "Month";

    public int CalendarGridRows => CalendarViewMode switch
    {
        "Week" => 1,
        "TwoWeek" => 2,
        _ => 6
    };

    public string CalendarHeaderLabel
    {
        get
        {
            if (CalendarViewMode == "Month")
            {
                return CalendarMonthLabel;
            }

            var start = GetCalendarGridStart();
            var end = start.AddDays((CalendarGridRows * 7) - 1);
            return start.Year == end.Year
                ? $"{start:MMM d} – {end:MMM d, yyyy}"
                : $"{start:MMM d, yyyy} – {end:MMM d, yyyy}";
        }
    }

    [ObservableProperty]
    private ObservableCollection<JournalTask> tasks = [];

    [ObservableProperty]
    private JournalTask? selectedTask;

    [ObservableProperty]
    private string newTaskTitle = string.Empty;

    [ObservableProperty]
    private bool isScheduleTaskModalOpen;

    [ObservableProperty]
    private JournalTask? taskBeingScheduled;

    [ObservableProperty]
    private DateTimeOffset? scheduleTaskDate = DateTimeOffset.Now.Date;

    [ObservableProperty]
    private TimeSpan? scheduleTaskTime;

    [ObservableProperty]
    private ObservableCollection<MemoryItem> memoryItems = [];

    [ObservableProperty]
    private ObservableCollection<MemoryItem> visibleMemoryItems = [];

    [ObservableProperty]
    private ObservableCollection<MemoryTransaction> memoryTransactions = [];

    [ObservableProperty]
    private MemoryItem? selectedMemoryItem;

    [ObservableProperty]
    private string memoryKey = string.Empty;

    [ObservableProperty]
    private string memoryValue = string.Empty;

    [ObservableProperty]
    private string selectedMemorySection = "user";

    [ObservableProperty]
    private string memoryStatus = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ChatMessage> chatMessages = [];

    [ObservableProperty]
    private ObservableCollection<ChatThread> chatThreads = [];

    [ObservableProperty]
    private ChatThread? selectedChatThread;

    [ObservableProperty]
    private string selectedChatTitle = string.Empty;

    [ObservableProperty]
    private bool isChatRenameOpen;

    [ObservableProperty]
    private string renameChatTitle = string.Empty;

    [ObservableProperty]
    private string chatSidebarStatus = string.Empty;

    private ChatThread? chatThreadBeingRenamed;

    [ObservableProperty]
    private string newChatTitle = string.Empty;

    [ObservableProperty]
    private string chatInput = string.Empty;

    public Task Initialization { get; }

    public bool IsThreadsPanelClosed => !IsThreadsPanelOpen;

    public bool IsToolsPanelClosed => !IsToolsPanelOpen;

    public bool IsChatView => CurrentView == "Chat";

    public bool IsMemoryView => CurrentView == "Memory";

    public bool IsCalendarView => CurrentView == "Calendar";

    public bool IsTasksView => CurrentView == "Tasks";

    public bool IsReviewView => CurrentView == "Review";

    public bool IsDatesMemorySection => SelectedMemorySection == "dates";

    public string MemorySectionDescription => SelectedMemorySection switch
    {
        "user" => "Identity, routines, health, interests, and personal details.",
        "projects" => "Active projects, status, tech stack, and blockers.",
        "people" => "Relationships, contacts, and people details.",
        "systems" => "Hardware, services, domains, and backups.",
        "tools" => "Development environment and tool preferences.",
        "goals" => "Short-term and long-term goals and decisions.",
        "knowledge" => "Learnings, skills, resources, and mistakes.",
        "context" => "Life status, environment, energy, and constraints.",
        "meta" => "System state, reflection style, and learned patterns.",
        "flex" => "Preferences, patterns, and uncategorized items.",
        "dates" => "Appointments, events, and durations on the calendar.",
        _ => "All memory items."
    };

    partial void OnIsThreadsPanelOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsThreadsPanelClosed));
    }

    partial void OnIsToolsPanelOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsToolsPanelClosed));
    }

    partial void OnCurrentViewChanged(string value)
    {
        OnPropertyChanged(nameof(IsChatView));
        OnPropertyChanged(nameof(IsMemoryView));
        OnPropertyChanged(nameof(IsCalendarView));
        OnPropertyChanged(nameof(IsTasksView));
        OnPropertyChanged(nameof(IsReviewView));
    }

    partial void OnSelectedMemorySectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsDatesMemorySection));
        OnPropertyChanged(nameof(MemorySectionDescription));
        RefreshVisibleMemory();
    }

    partial void OnSelectedMemoryItemChanged(MemoryItem? value)
    {
        if (value is null)
        {
            return;
        }

        MemoryKey = value.Key;
        MemoryValue = value.Value;
    }

    partial void OnSelectedChatThreadChanged(ChatThread? value)
    {
        if (value is null)
        {
            SelectedChatTitle = string.Empty;
            return;
        }

        SelectedChatTitle = value.Title;
        _ = LoadChatAsync();
    }

    [RelayCommand]
    private async Task CreateChatThreadAsync()
    {
        var title = BuildDefaultChatTitle();

        var thread = await database.AddChatThreadAsync(title);
        NewChatTitle = string.Empty;
        ChatSidebarStatus = string.Empty;
        await LoadThreadsAsync();
        var createdThread = ChatThreads.FirstOrDefault(item => item.Id == thread.Id);
        if (createdThread is not null)
        {
            BeginRenameChatThread(createdThread);
        }
    }

    private string BuildDefaultChatTitle()
    {
        var existingTitles = ChatThreads.Select(thread => thread.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existingTitles.Contains("New Chat"))
        {
            return "New Chat";
        }

        var index = 1;
        while (existingTitles.Contains($"New Chat {index}"))
        {
            index++;
        }

        return $"New Chat {index}";
    }

    public void BeginRenameChatThread(ChatThread thread)
    {
        SelectedChatThread = ChatThreads.FirstOrDefault(item => item.Id == thread.Id) ?? thread;
        chatThreadBeingRenamed = thread;
        RenameChatTitle = thread.Title;
        ChatSidebarStatus = string.Empty;
        IsChatRenameOpen = true;
    }

    public void CloseChatRename()
    {
        IsChatRenameOpen = false;
        RenameChatTitle = string.Empty;
        chatThreadBeingRenamed = null;
    }

    public async Task SaveChatRenameAsync()
    {
        if (chatThreadBeingRenamed is null || string.IsNullOrWhiteSpace(RenameChatTitle))
        {
            return;
        }

        var title = RenameChatTitle.Trim();
        await database.UpdateChatThreadTitleAsync(chatThreadBeingRenamed.Id, title);
        IsChatRenameOpen = false;
        RenameChatTitle = string.Empty;
        chatThreadBeingRenamed = null;
        await LoadThreadsAsync();
        ChatSidebarStatus = "Chat renamed.";
    }

    public async Task DeleteChatThreadAsync(ChatThread thread)
    {
        if (thread.IsMain)
        {
            ChatSidebarStatus = "Main Chat cannot be deleted.";
            return;
        }

        var wasSelected = SelectedThreadId == thread.Id;
        await database.DeleteChatThreadAsync(thread.Id);
        if (wasSelected)
        {
            SelectedChatThread = null;
        }

        await LoadThreadsAsync();
        await LoadChatAsync();
        ChatSidebarStatus = $"Deleted `{thread.Title}`.";
    }

    public async Task ClearChatThreadAsync(ChatThread thread)
    {
        await database.ClearChatMessagesAsync(thread.Id);
        if (SelectedThreadId == thread.Id)
        {
            await LoadChatAsync();
        }

        ChatSidebarStatus = $"Cleared `{thread.Title}`.";
    }

    public async Task SummarizeChatThreadAsync(ChatThread thread)
    {
        SelectedChatThread = ChatThreads.FirstOrDefault(item => item.Id == thread.Id) ?? thread;
        await UpdateChatSummaryAsync();
        ChatSidebarStatus = $"Summarized `{thread.Title}`.";
    }

    [RelayCommand]
    private async Task RenameSelectedChatThreadAsync()
    {
        if (SelectedChatThread is null || string.IsNullOrWhiteSpace(SelectedChatTitle))
        {
            return;
        }

        var title = SelectedChatTitle.Trim();
        if (string.Equals(title, SelectedChatThread.Title, StringComparison.Ordinal))
        {
            return;
        }

        await database.UpdateChatThreadTitleAsync(SelectedThreadId, title);
        await LoadThreadsAsync();
    }

    [RelayCommand]
    private async Task SaveChatRenameFromDialogAsync()
    {
        await SaveChatRenameAsync();
    }

    [RelayCommand]
    private void CloseChatRenameFromDialog()
    {
        CloseChatRename();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsStatus = string.Empty;
        RefreshVoiceModelStatus();
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void ShowChat()
    {
        CurrentView = "Chat";
    }

    [RelayCommand]
    private void ShowMemory()
    {
        CurrentView = "Memory";
    }

    [RelayCommand]
    private void ShowMemoryCategory(string category)
    {
        SelectedMemorySection = category;
    }

    [RelayCommand]
    private void ShowCalendar()
    {
        CurrentView = "Calendar";
    }

    [ObservableProperty]
    private ObservableCollection<ReviewItem> reviewItems = [];

    [ObservableProperty]
    private int openReviewCount;

    public string ReviewMenuHeader => OpenReviewCount > 0 ? $"Review ({OpenReviewCount})" : "Review";

    public bool HasNoReviewItems => OpenReviewCount == 0;

    partial void OnOpenReviewCountChanged(int value)
    {
        OnPropertyChanged(nameof(ReviewMenuHeader));
        OnPropertyChanged(nameof(HasNoReviewItems));
    }


    [RelayCommand]
    private void ShowTasks()
    {
        CurrentView = "Tasks";
    }

    [RelayCommand]
    private void OpenScheduleTaskModal(JournalTask task)
    {
        TaskBeingScheduled = task;
        ScheduleTaskDate = DateTimeOffset.Now.Date;
        ScheduleTaskTime = null;
        IsScheduleTaskModalOpen = true;
    }

    [RelayCommand]
    private void CloseScheduleTaskModal()
    {
        IsScheduleTaskModalOpen = false;
        TaskBeingScheduled = null;
    }

    [RelayCommand]
    private async Task ConfirmScheduleTaskAsync()
    {
        if (TaskBeingScheduled is null || ScheduleTaskDate is null)
        {
            return;
        }

        var date = ScheduleTaskDate.Value.Date;
        var time = ScheduleTaskTime ?? TimeSpan.Zero;
        var startsAt = new DateTimeOffset(date + time, TimeZoneInfo.Local.GetUtcOffset(date));

        await calendarService.AddEventAsync(TaskBeingScheduled.Title, startsAt, string.Empty, "manual");
        IsScheduleTaskModalOpen = false;
        TaskBeingScheduled = null;
        ScheduleTaskDate = DateTimeOffset.Now.Date;
        ScheduleTaskTime = null;
        await LoadEventsAsync();
    }

    [RelayCommand]
    private async Task DeleteSelectedTaskAsync(JournalTask task)
    {
        await taskService.DeleteAsync(task.Id);
        if (SelectedTask?.Id == task.Id)
        {
            SelectedTask = null;
        }

        await LoadTasksAsync();
    }

    [RelayCommand]
    private void ToggleThreadsPanel()
    {
        IsThreadsPanelOpen = !IsThreadsPanelOpen;
    }

    [RelayCommand]
    private void ToggleToolsPanel()
    {
        IsToolsPanelOpen = !IsToolsPanelOpen;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var apiKey = DeepSeekApiKey.Trim();
        var stored = string.IsNullOrWhiteSpace(apiKey) ? "" : dataProtection.ProtectString(apiKey);
        await database.SetSettingAsync("deepseek_api_key", stored);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", apiKey);
        SettingsStatus = string.IsNullOrWhiteSpace(apiKey)
            ? "DeepSeek API key cleared."
            : "DeepSeek API key saved locally.";
    }

    [RelayCommand]
    private async Task UpdateChatSummaryAsync()
    {
        if (SelectedChatThread is null || ChatMessages.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var transcript = new StringBuilder();
        foreach (var message in ChatMessages.Where(message => !string.IsNullOrWhiteSpace(message.Content)).TakeLast(80))
        {
            transcript.AppendLine($"[{message.Timestamp:O}] {message.Role}: {message.Content}");
        }

        var prompt = new StringBuilder();
        prompt.AppendLine("Summarize this chat for future Main Chat context.");
        prompt.AppendLine("Keep it compact but useful. Include durable context, user goals, preferences, decisions, important conclusions, and open loops. Omit filler. Do not invent facts.");
        prompt.AppendLine("Target 120-220 words. Plain text only.");

        if (!string.IsNullOrWhiteSpace(SelectedChatThread.Summary))
        {
            prompt.AppendLine();
            prompt.AppendLine("Previous summary:");
            prompt.AppendLine(SelectedChatThread.Summary);
        }

        prompt.AppendLine();
        prompt.AppendLine("Recent transcript:");
        prompt.AppendLine(transcript.ToString());

        var summary = new StringBuilder();
        var messages = new List<LlmMessage>
        {
            new("system", "You write compact, factual memory summaries for a personal chat hub.", now),
            new("user", prompt.ToString(), now)
        };

        await foreach (var token in llmProvider.ChatAsync(messages))
        {
            summary.Append(token);
        }

        var summaryText = summary.ToString().Trim();
        if (string.IsNullOrWhiteSpace(summaryText) || summaryText.StartsWith("Set DEEPSEEK_API_KEY", StringComparison.OrdinalIgnoreCase))
        {
            MemoryStatus = summaryText;
            return;
        }

        await database.UpdateChatThreadSummaryAsync(SelectedThreadId, summaryText);
        await LoadThreadsAsync();
    }

    [RelayCommand]
    private async Task SaveContextAsync()
    {
        await database.SetSettingAsync("primary_instructions", PrimaryInstructions);
        await database.SetSettingAsync("scratchpad", Scratchpad);
    }

    [RelayCommand]
    private async Task AddEventAsync()
    {
        if (string.IsNullOrWhiteSpace(EventTitle))
        {
            return;
        }

        var date = (EventDate ?? SelectedCalendarDate).Date;
        var time = EventTime ?? TimeSpan.Zero;
        var startsAt = new DateTimeOffset(date + time, TimeZoneInfo.Local.GetUtcOffset(date));

        await calendarService.AddEventAsync(EventTitle.Trim(), startsAt, EventDetails.Trim(), "manual");
        EventTitle = string.Empty;
        EventTime = null;
        EventDetails = string.Empty;
        await LoadEventsAsync();
    }

    [RelayCommand]
    private void SetCalendarViewMode(string mode)
    {
        CalendarViewMode = mode;
    }

    [RelayCommand]
    private void PreviousCalendarPeriod()
    {
        switch (CalendarViewMode)
        {
            case "Week":
                SelectedCalendarDate = SelectedCalendarDate.AddDays(-7);
                break;
            case "TwoWeek":
                SelectedCalendarDate = SelectedCalendarDate.AddDays(-14);
                break;
            default:
                CalendarMonth = CalendarMonth.AddMonths(-1);
                break;
        }
    }

    [RelayCommand]
    private void NextCalendarPeriod()
    {
        switch (CalendarViewMode)
        {
            case "Week":
                SelectedCalendarDate = SelectedCalendarDate.AddDays(7);
                break;
            case "TwoWeek":
                SelectedCalendarDate = SelectedCalendarDate.AddDays(14);
                break;
            default:
                CalendarMonth = CalendarMonth.AddMonths(1);
                break;
        }
    }

    [RelayCommand]
    private void GoToCalendarToday()
    {
        CalendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        SelectedCalendarDate = DateTime.Today;
    }

    [RelayCommand]
    private void SelectCalendarDay(CalendarDayCell day)
    {
        SelectedCalendarDate = day.Date;
        if (!day.IsCurrentMonth)
        {
            CalendarMonth = new DateTime(day.Date.Year, day.Date.Month, 1);
        }

        EventDate = null; // the day modal always adds to the selected day
        RebuildDayEditors();
        IsDayModalOpen = true;
    }

    [ObservableProperty]
    private bool isDayModalOpen;

    [ObservableProperty]
    private bool hasNoDayEvents = true;

    [ObservableProperty]
    private ObservableCollection<CalendarEventEditor> dayEditors = [];

    private void RebuildDayEditors()
    {
        DayEditors = new ObservableCollection<CalendarEventEditor>(
            CalendarEvents
                .Where(item => item.StartsAt.Date == SelectedCalendarDate.Date)
                .OrderBy(item => item.StartsAt)
                .Select(item => new CalendarEventEditor
                {
                    Id = item.Id,
                    Title = item.Title,
                    Details = item.Details,
                    Time = item.StartsAt.TimeOfDay
                }));
        HasNoDayEvents = DayEditors.Count == 0;
    }

    [RelayCommand]
    private void CloseDayModal() => IsDayModalOpen = false;

    [RelayCommand]
    private async Task SaveEventEditorAsync(CalendarEventEditor? editor)
    {
        if (editor is null || string.IsNullOrWhiteSpace(editor.Title))
        {
            return;
        }

        var local = SelectedCalendarDate.Date + (editor.Time ?? TimeSpan.Zero);
        var startsAt = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        var before = CalendarEvents.FirstOrDefault(e => e.Id == editor.Id);
        await calendarService.UpdateEventAsync(editor.Id, editor.Title.Trim(), startsAt, editor.Details?.Trim() ?? string.Empty, "manual");
        if (before is not null)
        {
            var oldText = $"{before.Title} @ {before.StartsAt:yyyy-MM-dd HH:mm}" + (before.Details.Length > 0 ? $" ({before.Details})" : string.Empty);
            var newText = $"{editor.Title.Trim()} @ {startsAt:yyyy-MM-dd HH:mm}" + (editor.Details?.Trim().Length > 0 ? $" ({editor.Details.Trim()})" : string.Empty);
            if (oldText != newText)
            {
                NoteCorrection("event_edit", $"event #{editor.Id}", oldText, newText, "edited by hand (was " + before.Source + ")");
            }
        }

        await LoadEventsAsync();
    }

    [RelayCommand]
    private async Task DeleteEventEditorAsync(CalendarEventEditor? editor)
    {
        if (editor is null)
        {
            return;
        }

        await calendarService.DeleteEventAsync(editor.Id);
        await LoadEventsAsync();
    }

    [RelayCommand]
    private async Task AddTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTaskTitle))
        {
            return;
        }

        await taskService.AddAsync(NewTaskTitle.Trim(), "manual");
        NewTaskTitle = string.Empty;
        await LoadTasksAsync();
    }

    [RelayCommand]
    private async Task ToggleSelectedTaskAsync(JournalTask task)
    {
        // IsDone already toggled by TwoWay checkbox binding; just persist.
        await taskService.SetDoneAsync(task.Id, task.IsDone);
    }

    [RelayCommand]
    private async Task SaveMemoryAsync()
    {
        if (string.IsNullOrWhiteSpace(MemoryKey))
        {
            return;
        }

        var type = memoryService.MemoryTypeForKey(MemoryKey.Trim());
        var previousValue = await database.GetMemoryValueAsync(MemoryKey.Trim());
        await memoryService.UpsertAsync(MemoryKey.Trim(), MemoryValue.Trim(), type, "manual");
        if (previousValue != MemoryValue.Trim())
        {
            NoteCorrection("memory_edit", MemoryKey.Trim(), previousValue ?? string.Empty, MemoryValue.Trim(), previousValue is null ? "added by hand" : "changed by hand");
        }

        await database.AddMemoryTransactionAsync("write", MemoryKey.Trim(), MemoryValue.Trim(), "success", "manual", type: type);
        MemoryKey = string.Empty;
        MemoryValue = string.Empty;
        await LoadMemoryAsync();
        await LoadMemoryTransactionsAsync();
    }

    [RelayCommand]
    private async Task DeleteSelectedMemoryAsync()
    {
        if (SelectedMemoryItem is null)
        {
            return;
        }

        await memoryService.DeleteAsync(SelectedMemoryItem.Key);
        NoteCorrection("memory_delete", SelectedMemoryItem.Key, SelectedMemoryItem.Value, string.Empty, "deleted by hand");
        await database.AddMemoryTransactionAsync("delete", SelectedMemoryItem.Key, string.Empty, "success", "manual", type: SelectedMemoryItem.Type);
        SelectedMemoryItem = null;
        MemoryKey = string.Empty;
        MemoryValue = string.Empty;
        await LoadMemoryAsync();
        await LoadMemoryTransactionsAsync();
    }

    [RelayCommand]
    private async Task DeleteSelectedCalendarEventAsync()
    {
        if (SelectedUpcomingCalendarEvent is null)
        {
            return;
        }

        await calendarService.DeleteEventAsync(SelectedUpcomingCalendarEvent.Id);
        SelectedUpcomingCalendarEvent = null;
        await LoadEventsAsync();
    }

    [RelayCommand]
    private async Task SendChatAsync()
    {
        // Sending while dictating must send the FINAL transcript, not the half-finished live text.
        if (dictationFinishing is { IsCompleted: false })
        {
            await dictationFinishing;
        }
        else if (IsListening)
        {
            dictationFinishing = FinishDictationAsync(polish: false);
            await dictationFinishing;
        }

        if (string.IsNullOrWhiteSpace(ChatInput))
        {
            return;
        }

        // Take the text and clear the box before any await, so two overlapping sends can't both read it.
        var now = DateTimeOffset.Now;
        var prompt = ChatInput.Trim();
        ChatInput = string.Empty;
        dictationActive = false;
        sendCount++;

        var fromDictation = boxHasDictation;
        boxHasDictation = false;
        if (fromDictation)
        {
            if (prompt == lastDictationSent && now - lastDictationSentAt < TimeSpan.FromSeconds(10))
            {
                devLog.Log("Duplicate dictated message suppressed", "voice");
                return;
            }

            lastDictationSent = prompt;
            lastDictationSentAt = now;
        }

        await SaveContextAsync();
        devLog.Log($"Sending message: {prompt.Length} chars", "chat");

        if (TryParsePrefixCommand(prompt, out var commandPrefix, out var commandBody))
        {
            await HandlePrefixCommandAsync(commandPrefix, commandBody, prompt, now);
            return;
        }

        if (await TryHandleMemoryConfirmationAsync(prompt, now))
        {
            return;
        }

        var userMessage = new ChatMessage { Role = "user", Content = prompt, Timestamp = now };
        ChatMessages.Add(userMessage);
        await database.AddChatMessageAsync(SelectedThreadId, userMessage.Role, userMessage.Content, userMessage.Timestamp);
        _ = clioState.RecordMessageAsync();
        var localCandidates = IsExplicitDeleteRequest(prompt)
            ? []
            : await ApplyLocalMemoryExtractionAsync(prompt, now);
        var routedAgent = router.Route(string.Empty, prompt);
        MemoryStatus = $"Routed to: {routedAgent}";
        devLog.Log($"Chat routed to {routedAgent}: {prompt[..Math.Min(prompt.Length, 60)]}", "routing");

        var assistant = new ChatMessage { Role = "assistant", Content = string.Empty, Timestamp = DateTimeOffset.Now };
        ChatMessages.Add(assistant);

        await LoadEventsAsync();
        await LoadTasksAsync();
        // "@Database-Architect ..." answers as that persona for this reply.
        AgentFile? agent = null;
        if (agentCatalog.TryMatch(prompt, out var matchedAgent, out _))
        {
            agent = matchedAgent;
            turnAgentPrompt = AgentCatalog.BuildPrompt(matchedAgent);
        }
        else if (Agents.ClioRouter.FocusFor(routedAgent) is { } focus)
        {
            turnAgentPrompt = focus;
        }

        var llmMessages = await BuildLlmMessagesAsync(prompt, now);

        var reply = await StreamReplyAsync(llmMessages, assistant);
        await LogLlmExchangeAsync(llmMessages, reply);
        await RunSkillLoopAsync(llmMessages, reply, assistant);

        await ApplyStructuredMemoryAsync(assistant, prompt);
        if (agent is not null)
        {
            assistant.ActionBadges = new ObservableCollection<ActionBadge>(
                assistant.ActionBadges.Append(MakeBadge("@", agent.Name, "#30343B")));
        }

        await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
        SpeakAssistantReply(assistant.Content);
        _ = RunAutoExtractionAsync(prompt, assistant, now);

        await LoadThreadsAsync();
    }

    private static bool TryParsePrefixCommand(string input, out string prefix, out string body)
    {
        prefix = string.Empty;
        body = string.Empty;

        if (input.Length < 3)
        {
            return false;
        }

        var candidate = input[..2];
        if (candidate is "++" or "--" or "==" or "__" or "@@")
        {
            prefix = candidate;
            body = input[2..].Trim();
            return !string.IsNullOrWhiteSpace(body);
        }

        return false;
    }


    private async Task<bool> TryHandleMemoryConfirmationAsync(string prompt, DateTimeOffset timestamp)
    {
        if (SelectedChatThread?.IsMain != true)
        {
            return false;
        }

        var normalized = prompt.Trim().ToLowerInvariant();
        var isExplicitConfirm = normalized is "store it" or "save it" or "confirm" or "update that";
        var isExplicitReject = normalized is "skip" or "ignore it";
        var isGenericConfirm = IsAffirmativeReply(normalized);
        var isGenericReject = IsNegativeReply(normalized);
        var isConfirm = isExplicitConfirm || isGenericConfirm;
        var isReject = isExplicitReject || isGenericReject;
        if (!isConfirm && !isReject)
        {
            return false;
        }

        if (!isExplicitConfirm && !isExplicitReject && !IsCurrentChatAwaitingMemoryConfirmation())
        {
            return false;
        }

        var pendingTransactions = (await database.GetMemoryTransactionsAsync())
            .Where(item => item.Status == "pending_review")
            .ToList();

        var targetTransactions = new List<MemoryTransaction>();
        if (pendingMemoryConfirmationKeys is { Count: > 0 })
        {
            foreach (var key in pendingMemoryConfirmationKeys)
            {
                var match = pendingTransactions
                    .FirstOrDefault(item => string.Equals(item.Field, key, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    targetTransactions.Add(match);
                }
            }
        }

        if (targetTransactions.Count == 0)
        {
            return false;
        }

        pendingMemoryConfirmationKeys = null;
        MemoryToastVisible = false;

        var userMessage = new ChatMessage { Role = "user", Content = prompt, Timestamp = timestamp };
        ChatMessages.Add(userMessage);
        await database.AddChatMessageAsync(SelectedThreadId, userMessage.Role, userMessage.Content, userMessage.Timestamp);

        var responses = new List<string>();
        foreach (var transaction in targetTransactions)
        {
            responses.Add(isConfirm
                ? await ConfirmMemoryTransactionAsync(transaction)
                : await RejectMemoryTransactionAsync(transaction));
        }

        var response = string.Join(" ", responses);

        var assistant = new ChatMessage { Role = "assistant", Content = response, Timestamp = DateTimeOffset.Now };
        ChatMessages.Add(assistant);
        await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
        await LoadThreadsAsync();
        return true;
    }

    private bool IsCurrentChatAwaitingMemoryConfirmation()
    {
        var lastAssistantMessage = ChatMessages
            .LastOrDefault(message => message.Role == "assistant" && !string.IsNullOrWhiteSpace(message.Content));

        return lastAssistantMessage?.Content.TrimStart().StartsWith("Should I store", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task<string> ConfirmMemoryTransactionAsync(MemoryTransaction transaction)
    {
        await database.UpsertMemoryAsync(transaction.Field, transaction.Value, transaction.Type);
        await database.UpdateMemoryTransactionStatusAsync(transaction.Id, "success");
        await LoadMemoryAsync();
        await LoadMemoryTransactionsAsync();
        return $"Stored `{transaction.Field}`.";
    }

    private async Task<string> RejectMemoryTransactionAsync(MemoryTransaction transaction)
    {
        await database.UpdateMemoryTransactionStatusAsync(transaction.Id, "failed", "Dismissed by user.");
        await LoadMemoryTransactionsAsync();
        return $"Skipped `{transaction.Field}`.";
    }

    [RelayCommand]
    private async Task AcceptMemoryToastAsync()
    {
        await ResolveMemoryToastAsync(confirm: true);
    }

    [RelayCommand]
    private async Task DismissMemoryToastAsync()
    {
        await ResolveMemoryToastAsync(confirm: false);
    }

    private async Task ResolveMemoryToastAsync(bool confirm)
    {
        if (pendingMemoryConfirmationKeys is null)
        {
            MemoryToastVisible = false;
            return;
        }

        var keys = pendingMemoryConfirmationKeys;
        pendingMemoryConfirmationKeys = null;
        MemoryToastVisible = false;

        var pendingTransactions = (await database.GetMemoryTransactionsAsync())
            .Where(item => item.Status == "pending_review")
            .ToList();

        var targetTransactions = pendingTransactions
            .Where(item => keys.Contains(item.Field))
            .ToList();

        if (targetTransactions.Count > 0)
        {
            var responses = new List<string>();
            foreach (var transaction in targetTransactions)
            {
                responses.Add(confirm
                    ? await ConfirmMemoryTransactionAsync(transaction)
                    : await RejectMemoryTransactionAsync(transaction));
            }

            var assistant = new ChatMessage { Role = "assistant", Content = string.Join(" ", responses), Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(assistant);
            await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);
            await LoadThreadsAsync();
        }

        if (pendingMemoryCandidates is { Count: > 0 })
        {
            var queuedMemory = pendingMemoryCandidates;
            pendingMemoryCandidates = null;
            ShowMemoryConfirmationToast(queuedMemory);
        }
    }

    private static readonly Regex CalendarRelatedRegex = new(
        @"\b(calendar|schedule|appointment|appt|event|events|reminder|reminders|meeting|pedicure|haircut|dentist|today|tomorrow|tonight|monday|tuesday|wednesday|thursday|friday|saturday|sunday|week|month|\d{1,2}\s*(am|pm)|\d{1,2}:\d{2}|\d{1,2}\/\d{1,2})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsAffirmativeReply(string normalized)
    {
        return normalized is "yes" or "yeah" or "yep" or "y" or "sure" or "ok" or "okay" or "do it"
            || normalized.StartsWith("yes ", StringComparison.Ordinal)
            || normalized.StartsWith("yeah ", StringComparison.Ordinal)
            || normalized.StartsWith("yep ", StringComparison.Ordinal)
            || normalized.StartsWith("sure ", StringComparison.Ordinal)
            || normalized.StartsWith("ok ", StringComparison.Ordinal)
            || normalized.StartsWith("okay ", StringComparison.Ordinal);
    }

    private static bool IsNegativeReply(string normalized)
    {
        return normalized is "no" or "nope" or "n" or "don't" or "dont" or "not now"
            || normalized.StartsWith("no ", StringComparison.Ordinal)
            || normalized.StartsWith("nope ", StringComparison.Ordinal)
            || normalized.StartsWith("don't ", StringComparison.Ordinal)
            || normalized.StartsWith("dont ", StringComparison.Ordinal);
    }

    [RelayCommand]
    private async Task ClearConversationAsync()
    {
        ChatMessages.Clear();
        await database.ClearChatMessagesAsync(SelectedThreadId);
    }

    private async Task LoadAsync()
    {
        PrimaryInstructions = await database.GetSettingAsync("primary_instructions", PrimaryInstructions);
        Scratchpad = await database.GetSettingAsync("scratchpad");
        SpeakReplies = await database.GetSettingAsync("voice_speak_replies") == "1";
        AutoSendVoice = await database.GetSettingAsync("voice_auto_send") == "1";
        if (double.TryParse(await database.GetSettingAsync("voice_speed"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var savedSpeed))
        {
            VoiceSpeed = Math.Clamp(savedSpeed, 0.6, 1.6);
        }

        async Task<double?> LoadVoiceNumberAsync(string key, double min, double max) =>
            double.TryParse(await database.GetSettingAsync(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? Math.Clamp(v, min, max)
                : null;

        keyCanonicalizer.LoadAliases(await database.GetSettingAsync("memory_key_aliases"));
        if (updateInstaller.ReadAndClearResult() is { } updateResult)
        {
            var text = updateResult.Ok
                ? $"CLIo was updated to version {AppVersion.Text}."
                : $"The update to {updateResult.Version} did not finish: {updateResult.Message} You're on version {AppVersion.Text}.";
            await database.AddChatMessageAsync(1, "assistant", text, DateTimeOffset.Now);
        }

        await LoadReviewAsync();
        PolishDictation = await database.GetSettingAsync("voice_polish", "1") != "0";
        VoiceCorrectionsText = await database.GetSettingAsync("voice_corrections");
        VoiceCorrections.Corrected += change => devLog.Log($"Voice correction: {change}", "voice");
        var installId = await database.GetSettingAsync("voice_install_id");
        if (!Guid.TryParse(installId, out _))
        {
            installId = Guid.NewGuid().ToString();
            await database.SetSettingAsync("voice_install_id", installId);
        }

        VoiceAssets.InstallId = installId;
        LocalSpeechOnly = await database.GetSettingAsync("voice_local_only") == "1";
        // Never saved => use the built-in Worker so dictation works with zero setup.
        // A saved blank value (user cleared the box) still means local-only.
        const string unsetMarker = "\u0001unset";
        var storedSttUrl = await database.GetSettingAsync("voice_stt_url", unsetMarker);
        SttServerUrl = storedSttUrl == unsetMarker ? DefaultSttServerUrl : storedSttUrl;
        SttServerModel = await database.GetSettingAsync("voice_stt_model");
        var storedSttKey = await database.GetSettingAsync("voice_stt_key");
        if (!string.IsNullOrWhiteSpace(storedSttKey))
        {
            sttKeyLoading = true;
            try { SttServerKey = dataProtection.UnprotectString(storedSttKey); }
            catch { SttServerKey = string.Empty; }
            finally { sttKeyLoading = false; }
        }
        VoiceExpressiveness = await LoadVoiceNumberAsync("voice_noise_scale", 0, 1.5) ?? VoiceExpressiveness;
        VoiceCadence = await LoadVoiceNumberAsync("voice_noise_w", 0, 1.5) ?? VoiceCadence;
        VoicePause = await LoadVoiceNumberAsync("voice_sentence_silence", 0, 1.0) ?? VoicePause;
        var storedKey = await database.GetSettingAsync("deepseek_api_key");
        if (!string.IsNullOrWhiteSpace(storedKey))
        {
            try { DeepSeekApiKey = dataProtection.UnprotectString(storedKey); }
            catch { DeepSeekApiKey = storedKey; } // fallback for pre-encryption keys
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", DeepSeekApiKey);
        }

        await LoadThreadsAsync();
        await LoadChatAsync();
        await LoadEventsAsync();
        await LoadTasksAsync();
        await LoadMemoryAsync();
        await LoadMemoryTransactionsAsync();
        await MaybeTrimChatHistoryAsync();
        await clioState.RecordStartupAsync(SystemPromptBuilder.Version);
        devLog.Log($"CLIo v{SystemPromptBuilder.Version} started, session #{await clioState.GetAsync("session_count")}", "startup");
        runtimeLog.Record("app", "start", "ok", $"CLIo v{SystemPromptBuilder.Version}, session #{await clioState.GetAsync("session_count")}");
        _ = RunUpdateLoopAsync();
        _ = RunTidyLoopAsync();
    }

    private const int MaxMessagesPerThread = 400;

    private async Task MaybeTrimChatHistoryAsync()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var lastCheck = await database.GetSettingAsync("last_chat_trim_check");
        if (lastCheck == today)
        {
            return;
        }

        await database.SetSettingAsync("last_chat_trim_check", today);

        var trimmedThreads = await database.TrimOversizedThreadsAsync(MaxMessagesPerThread);
        if (trimmedThreads > 0)
        {
            await LoadChatAsync();
        }
    }

    private async Task LoadThreadsAsync()
    {
        var selectedThreadId = SelectedChatThread?.Id;
        ChatThreads = new ObservableCollection<ChatThread>(await database.GetChatThreadsAsync());
        SelectedChatThread = ChatThreads.FirstOrDefault(item => item.Id == selectedThreadId)
            ?? ChatThreads.FirstOrDefault(item => item.IsMain)
            ?? ChatThreads.FirstOrDefault();
    }

    private async Task LoadChatAsync()
    {
        var previousWidgets = ChatMessages
            .Where(message => message.HasCalendarWidget)
            .ToLookup(message => (message.Role, message.Timestamp));

        var messages = await database.GetChatMessagesAsync(SelectedThreadId, limit: 0);
        foreach (var message in messages)
        {
            message.Content = RemoveLeakedTimestampLines(message.Content);

            var previous = previousWidgets[(message.Role, message.Timestamp)].FirstOrDefault();
            if (previous is not null)
            {
                message.CalendarWidgetLabel = previous.CalendarWidgetLabel;
                message.CalendarWidgetColumns = previous.CalendarWidgetColumns;
                message.CalendarWidgetDays = previous.CalendarWidgetDays;
            }
        }

        ChatMessages = new ObservableCollection<ChatMessage>(messages);
    }

    private async Task LoadEventsAsync()
    {
        CalendarEvents = new ObservableCollection<CalendarEvent>(await calendarService.GetAllEventsAsync());
        UpcomingCalendarEvents = new ObservableCollection<CalendarEvent>(
            CalendarEvents.Where(item => item.StartsAt.Date >= DateTime.Today).OrderBy(item => item.StartsAt)
                .Concat(CalendarEvents.Where(item => item.StartsAt.Date < DateTime.Today).OrderByDescending(item => item.StartsAt)));
        BuildCalendarGrid();
    }

    partial void OnCalendarMonthChanged(DateTime value)
    {
        OnPropertyChanged(nameof(CalendarMonthLabel));
        OnPropertyChanged(nameof(CalendarHeaderLabel));
        BuildCalendarGrid();
    }

    partial void OnSelectedCalendarDateChanged(DateTime value)
    {
        OnPropertyChanged(nameof(SelectedCalendarDateLabel));
        OnPropertyChanged(nameof(CalendarHeaderLabel));
        BuildCalendarGrid();
    }

    partial void OnCalendarViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCalendarWeekView));
        OnPropertyChanged(nameof(IsCalendarTwoWeekView));
        OnPropertyChanged(nameof(IsCalendarMonthView));
        OnPropertyChanged(nameof(CalendarGridRows));
        OnPropertyChanged(nameof(CalendarHeaderLabel));
        BuildCalendarGrid();
    }

    private DateTime GetCalendarGridStart()
    {
        if (CalendarViewMode == "Month")
        {
            var firstOfMonth = new DateTime(CalendarMonth.Year, CalendarMonth.Month, 1);
            return firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);
        }

        return SelectedCalendarDate.AddDays(-(int)SelectedCalendarDate.DayOfWeek);
    }

    private void BuildCalendarGrid()
    {
        var gridStart = GetCalendarGridStart();
        var totalDays = CalendarGridRows * 7;
        var eventsByDate = CalendarEvents
            .GroupBy(item => item.StartsAt.Date)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CalendarEvent>)group.OrderBy(item => item.StartsAt).ToList());

        var days = new List<CalendarDayCell>(totalDays);
        for (var i = 0; i < totalDays; i++)
        {
            var date = gridStart.AddDays(i);
            var dayEvents = eventsByDate.TryGetValue(date, out var events) ? events : [];
            days.Add(new CalendarDayCell
            {
                Date = date,
                IsCurrentMonth = CalendarViewMode != "Month" || date.Month == CalendarMonth.Month,
                IsToday = date == DateTime.Today,
                IsSelected = date == SelectedCalendarDate,
                HasEvents = dayEvents.Count > 0,
                Events = dayEvents
            });
        }

        CalendarDays = new ObservableCollection<CalendarDayCell>(days);
        SelectedDayEvents = new ObservableCollection<CalendarEvent>(
            CalendarEvents.Where(item => item.StartsAt.Date == SelectedCalendarDate).OrderBy(item => item.StartsAt));
        if (IsDayModalOpen)
        {
            RebuildDayEditors();
        }
    }

    private async Task LoadTasksAsync()
    {
        Tasks = new ObservableCollection<JournalTask>(await taskService.GetAllAsync());
    }

    private async Task LoadMemoryAsync()
    {
        MemoryItems = new ObservableCollection<MemoryItem>(await memoryService.GetAllMemoryAsync());
        RefreshVisibleMemory();
    }

    private async Task LoadMemoryTransactionsAsync()
    {
        MemoryTransactions = new ObservableCollection<MemoryTransaction>(await memoryService.GetTransactionsAsync());
        RefreshVisibleMemory();
    }

    private void RefreshVisibleMemory()
    {
        VisibleMemoryItems = new ObservableCollection<MemoryItem>(MemoryItems.Where(ShouldShowMemoryItem));
    }

    private bool ShouldShowMemoryItem(MemoryItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Value))
            return false;

        if (SelectedMemorySection == "dates")
            return false;

        return item.Key.StartsWith(SelectedMemorySection + ":", StringComparison.OrdinalIgnoreCase);
    }

    private static string MemoryTypeForSection(string section)
    {
        return ValidCategories.Contains(section) ? section : "flex";
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

    private static string BuildPreferenceKey(string value) => "flex:preference:" + Slugify(value);

    private static string BuildPatternKey(string value) => "flex:pattern:" + Slugify(value);

    private int SelectedThreadId => SelectedChatThread?.Id ?? 1;

    /// <summary>
    /// Streams a model reply into the chat message. A reply that begins "SKILL CALL" is held back so the
    /// user never sees the raw block flash by; everything else streams token by token as before.
    /// </summary>
    private async Task<string> StreamReplyAsync(IReadOnlyList<LlmMessage> messages, ChatMessage assistant)
    {
        const string marker = "SKILL CALL";
        var buffer = new StringBuilder();
        var revealed = false;
        assistant.Content = string.Empty;

        await foreach (var token in llmProvider.ChatAsync(messages))
        {
            buffer.Append(token);
            if (revealed)
            {
                assistant.Content += token;
                continue;
            }

            var head = buffer.ToString().TrimStart();
            var couldBeSkillCall = head.Length < marker.Length
                ? marker.StartsWith(head, StringComparison.OrdinalIgnoreCase)
                : head.StartsWith(marker, StringComparison.OrdinalIgnoreCase);
            if (!couldBeSkillCall)
            {
                revealed = true;
                assistant.Content = buffer.ToString();
            }
        }

        if (!revealed)
        {
            assistant.Content = buffer.ToString();
        }

        return buffer.ToString();
    }

    /// <summary>
    /// If the model replied with a SKILL CALL block, run the skills and let the model answer from the
    /// results, for at most <see cref="SkillRegistry.MaxRounds"/> rounds. The final answer ends up in
    /// <paramref name="assistant"/>.
    /// </summary>
    private async Task RunSkillLoopAsync(IReadOnlyList<LlmMessage> messages, string reply, ChatMessage assistant)
    {
        var conversation = messages.ToList();
        for (var round = 0; round < SkillRegistry.MaxRounds; round++)
        {
            var calls = skillRegistry.ParseCalls(reply);
            if (calls.Count == 0)
            {
                break;
            }

            assistant.Content = skillRegistry.Describe(calls[0]) + (calls.Count > 1 ? $" (+{calls.Count - 1} more)" : string.Empty);
            devLog.Log($"Skill round {round + 1}: {string.Join("; ", calls.Select(c => $"{c.Name}({c.Input})"))}", "skills");

            var results = await skillRegistry.RunAsync(calls);
            conversation.Add(new LlmMessage("assistant", reply, DateTimeOffset.Now));
            conversation.Add(new LlmMessage("user", results, DateTimeOffset.Now));

            using (LlmCall.Use("chat+skill"))
            {
                reply = await StreamReplyAsync(conversation, assistant);
            }

            await LogLlmExchangeAsync(conversation, reply);
        }

        if (reply.Contains("SKILL CALL", StringComparison.OrdinalIgnoreCase))
        {
            // Still asking for tools after the last round: show whatever text came before the block.
            var visible = skillRegistry.StripCalls(reply);
            assistant.Content = visible.Length > 0 ? visible : "I couldn't get that information right now.";
        }
    }

    private async Task<IReadOnlyList<LlmMessage>> BuildLlmMessagesAsync(string userPrompt, DateTimeOffset promptTimestamp)
    {
        var selfContext = await clioState.BuildSelfContextAsync();
        var systemPrompt = promptBuilder.Build(new SystemPromptContext
        {
            PrimaryInstructions = PrimaryInstructions,
            ActiveChatTitle = SelectedChatThread?.Title ?? "Main Chat",
            Now = promptTimestamp,
            Scratchpad = Scratchpad,
            MemoryItems = MemoryItems.ToList(),
            CalendarEvents = CalendarEvents.ToList(),
            Tasks = Tasks.ToList(),
            SelfContext = selfContext,
            SkillsPrompt = skillRegistry.BuildPromptSection()
        });

        var context = new StringBuilder(systemPrompt);
        if (turnAgentPrompt is not null)
        {
            context.AppendLine(turnAgentPrompt);
            turnAgentPrompt = null;
        }

        if (SelectedChatThread?.IsMain == true)
        {
            var otherThreadContexts = await database.GetOtherThreadContextAsync(SelectedThreadId);
            if (otherThreadContexts.Count > 0)
            {
                context.AppendLine();
                context.AppendLine("Summaries and recent context from other chats:");
                foreach (var threadContext in otherThreadContexts)
                {
                    context.AppendLine(threadContext);
                    context.AppendLine();
                }
            }
        }
        else
        {
            var mainThreadContext = await database.GetThreadContextAsync(1, messageLimit: 16);
            if (!string.IsNullOrWhiteSpace(mainThreadContext))
            {
                context.AppendLine();
                context.AppendLine("Shared Main Chat context available to this scoped chat:");
                context.AppendLine(mainThreadContext);
            }
        }

        var messages = new List<LlmMessage> { new("system", context.ToString(), promptTimestamp) };
        foreach (var message in ChatMessages.Where(message => message.Content != string.Empty).TakeLast(30))
        {
            messages.Add(new LlmMessage(message.Role, message.Content, message.Timestamp));
        }

        if (messages.LastOrDefault()?.Content != userPrompt)
        {
            messages.Add(new LlmMessage("user", userPrompt, promptTimestamp));
        }

        return messages;
    }

    private static readonly string LlmLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLIo", "llm_log.txt");

    private static async Task LogLlmExchangeAsync(IReadOnlyList<LlmMessage> messages, string response)
    {
        try
        {
            var log = new StringBuilder();
            log.AppendLine($"===== {DateTimeOffset.Now:O} =====");
            foreach (var message in messages)
            {
                log.AppendLine($"--- {message.Role} ---");
                log.AppendLine(message.Content);
            }

            log.AppendLine("--- response ---");
            log.AppendLine(response);
            log.AppendLine();

            Directory.CreateDirectory(Path.GetDirectoryName(LlmLogPath)!);
            await File.AppendAllTextAsync(LlmLogPath, log.ToString());
        }
        catch
        {
            // best-effort debug log only
        }
    }

    private async Task<bool> ApplyStructuredMemoryAsync(ChatMessage assistant, string prompt)
    {
        var parsed = ParseAssistantResponse(assistant.Content);
        assistant.Content = parsed.VisibleContent;

        var calendarDirective = parsed.CalendarDirective ?? InferCalendarDirectiveFromPrompt(prompt);

        // Strip the raw ASCII calendar block from the visible text now, but defer
        // rendering the inline widget until AFTER all calendar mutations below so the
        // widget reflects the same DB state as the main calendar view.
        if (calendarDirective is not null)
        {
            assistant.Content = StripAsciiCalendarBlocks(assistant.Content);
        }

        var notes = new List<string>();

        // If the prompt is clearly a move/reschedule, any SCHEDULE block the LLM emitted is
        // almost certainly a misfire that would duplicate the event - suppress adds and route
        // through the move handler below instead.
        var isExplicitMove = parsed.MoveCandidates.Count == 0 && IsExplicitMoveRequest(prompt);

        var addedCount = 0;

        if (parsed.ScheduleCandidates.Count > 0 && !isExplicitMove)
        {
            foreach (var candidate in parsed.ScheduleCandidates)
            {
                if (DateTimeOffset.TryParse(candidate.When, out var startsAt))
                {
                    await calendarService.AddEventAsync(candidate.Title, startsAt, candidate.Details);
                    addedCount++;
                }
            }

            if (addedCount > 0)
            {
                await LoadEventsAsync();
                notes.Add($"added {addedCount} event(s) to the calendar");
            }
        }

        if (parsed.TaskCandidates.Count > 0)
        {
            foreach (var candidate in parsed.TaskCandidates)
            {
                await taskService.AddAsync(candidate.Title);
            }

            await LoadTasksAsync();
            notes.Add($"added {parsed.TaskCandidates.Count} task(s)");
        }

        var completedCount = 0;

        if (parsed.TaskCompleteCandidates.Count > 0)
        {
            foreach (var candidate in parsed.TaskCompleteCandidates)
            {
                if (await taskService.CompleteByTitleAsync(candidate.Title))
                {
                    completedCount++;
                }
            }

            if (completedCount > 0)
            {
                await LoadTasksAsync();
                notes.Add($"marked {completedCount} task(s) complete");
            }
        }

        var deleteCandidates = parsed.DeleteCandidates;
        var isExplicitDelete = false;
        if (deleteCandidates.Count == 0 && IsExplicitDeleteRequest(prompt))
        {
            var hint = ExtractDeleteTitleHint(prompt);
            if (!string.IsNullOrWhiteSpace(hint))
            {
                var when = ExtractDateHint(hint, out var titleWithoutDate);
                deleteCandidates = [new DeleteCandidate(titleWithoutDate, when)];
                isExplicitDelete = true;
            }
        }

        var deletedCount = 0;
        var notFoundTitles = new List<string>();

        if (deleteCandidates.Count > 0)
        {
            foreach (var candidate in deleteCandidates)
            {
                var matches = ResolveTargetEvents(candidate.Title, candidate.When);

                if (matches.Count == 0)
                {
                    notFoundTitles.Add(candidate.Title);
                    continue;
                }

                foreach (var match in matches)
                {
                    await calendarService.DeleteEventAsync(match.Id);
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                await LoadEventsAsync();
                notes.Add($"removed {deletedCount} event(s) from the calendar");
            }

            if (notFoundTitles.Count > 0)
            {
                notes.Add($"couldn't find a matching event for: {string.Join(", ", notFoundTitles)}");
            }
        }

        var movedCount = 0;
        var notMovedTitles = new List<string>();

        if (parsed.MoveCandidates.Count > 0)
        {
            foreach (var candidate in parsed.MoveCandidates)
            {
                var matches = ResolveTargetEvents(candidate.Title, candidate.When);

                if (matches.Count != 1 || !DateTimeOffset.TryParse(candidate.NewWhen, out var newStartsAt))
                {
                    notMovedTitles.Add(candidate.Title);
                    continue;
                }

                await calendarService.UpdateEventStartAsync(matches[0].Id, newStartsAt);
                movedCount++;
            }

            if (movedCount > 0)
            {
                await LoadEventsAsync();
                notes.Add($"moved {movedCount} event(s) on the calendar");
            }

            if (notMovedTitles.Count > 0)
            {
                notes.Add($"couldn't move: {string.Join(", ", notMovedTitles)}");
            }
        }
        else if (isExplicitMove)
        {
            // LLM didn't emit CALENDAR MOVE — infer from prompt.
            // Keep the event's existing time; only shift the date.
            var hint = ExtractMoveTitleHint(prompt);
            var newDate = ExtractFlexibleDate(hint, out var titleOnly);
            if (!string.IsNullOrWhiteSpace(titleOnly) && newDate is not null)
            {
                var matches = FindMatchingEvents(titleOnly, null);
                if (matches.Count == 1)
                {
                    var existing = matches[0];
                    var newStart = new DateTimeOffset(newDate.Value.Date + existing.StartsAt.TimeOfDay, existing.StartsAt.Offset);
                    await calendarService.UpdateEventStartAsync(existing.Id, newStart);
                    await LoadEventsAsync();
                    movedCount++;
                    notes.Add("moved 1 event(s) on the calendar");
                }
                else
                {
                    notes.Add(matches.Count == 0
                        ? $"couldn't find an event matching \"{titleOnly}\" to move"
                        : $"more than one event matches \"{titleOnly}\" - tell me which one to move");
                }
            }
            else if (TryExtractRelativeOffset(prompt, out _, out var relOffset))
            {
                // Relative-delta move: "push dentist back a week", "delay pool party by 3 days"
                // Run offset detection on original prompt (direction words stripped from hint).
                // Use cleaned hint for title matching.
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    var matches = FindMatchingEvents(hint, null);
                    if (matches.Count == 1)
                    {
                        var existing = matches[0];
                        var newStart = existing.StartsAt.Add(relOffset);
                        await calendarService.UpdateEventStartAsync(existing.Id, newStart);
                        await LoadEventsAsync();
                        movedCount++;
                        notes.Add("moved 1 event(s) on the calendar");
                    }
                    else
                    {
                        notes.Add(matches.Count == 0
                            ? $"couldn't find an event matching \"{hint}\" to move"
                            : $"more than one event matches \"{hint}\" - tell me which one to move");
                    }
                }
            }
        }

        var editedCount = 0;
        var notEditedTitles = new List<string>();

        if (parsed.EditCandidates.Count > 0)
        {
            foreach (var candidate in parsed.EditCandidates)
            {
                var matches = ResolveTargetEvents(candidate.Title, candidate.When);
                if (matches.Count != 1)
                {
                    notEditedTitles.Add(candidate.Title);
                    continue;
                }

                var existing = matches[0];
                var newTitle = candidate.NewTitle ?? existing.Title;
                var newDetails = candidate.NewDetails ?? existing.Details;
                var newStart = existing.StartsAt;
                if (candidate.NewWhen is not null && !DateTimeOffset.TryParse(candidate.NewWhen, out newStart))
                {
                    newStart = existing.StartsAt;
                }

                await calendarService.UpdateEventAsync(existing.Id, newTitle, newStart, newDetails);
                editedCount++;
            }

            if (editedCount > 0)
            {
                await LoadEventsAsync();
                notes.Add($"updated {editedCount} event(s) on the calendar");
            }

            if (notEditedTitles.Count > 0)
            {
                notes.Add($"couldn't update: {string.Join(", ", notEditedTitles)}");
            }
        }

        // Now that every calendar mutation (add/delete/move/edit) has been persisted and
        // CalendarEvents reloaded, render the inline widget from the same fresh state.
        if (calendarDirective is not null)
        {
            await LoadEventsAsync();
            ApplyCalendarDirective(assistant, calendarDirective);
        }

        if (parsed.MemoryCandidates.Count > 0 && !isExplicitDelete)
        {
            var pendingMemory = await ApplyMemoryCandidatesAsync(parsed.MemoryCandidates, $"chat_thread:{SelectedThreadId};assistant:{assistant.Timestamp:O}");
            if (pendingMemory.Count > 0 && SelectedChatThread?.IsMain == true)
            {
                QueueOrShowMemoryConfirmation(pendingMemory);
                notes.Add("waiting on confirmation to store memory");
            }
            else if (pendingMemory.Count == 0)
            {
                notes.Add($"saved {parsed.MemoryCandidates.Count} memory item(s)");
            }
            else
            {
                notes.Add("noted memory for review");
            }
        }

        if (isExplicitDelete)
        {
            assistant.Content = deletedCount > 0
                ? $"Done - removed {deletedCount} event(s) from your calendar."
                : $"I couldn't find a matching calendar event for \"{deleteCandidates[0].Title}\".";
        }
        else
        {
            var corrections = new List<string>();
            if (notFoundTitles.Count > 0)
            {
                corrections.Add($"Actually, I couldn't find a calendar event matching: {string.Join(", ", notFoundTitles)}. Nothing was deleted for that - double check the title/date on your calendar.");
            }

            if (notMovedTitles.Count > 0)
            {
                corrections.Add($"Actually, I couldn't move: {string.Join(", ", notMovedTitles)}. Nothing was changed for that - double check the title/date on your calendar.");
            }

            if (corrections.Count > 0)
            {
                // Replace rather than append - the LLM's own text may have already claimed
                // success/failure that contradicts this correction, which reads as a confusing
                // two-part message. The correction is the accurate one.
                assistant.Content = string.Join(" ", corrections);
            }
        }

        if (notes.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(assistant.Content))
            {
                assistant.Content = "Done - " + string.Join("; ", notes) + ".";
            }

            MemoryStatus = "Inline capture: " + string.Join("; ", notes) + ".";
        }

        // Stamp action badges onto the message card (assign whole collection to fire property notification)
        var badges = new List<ActionBadge>();
        if (addedCount > 0)
            badges.Add(MakeBadge("📅", $"+{addedCount} cal", "#30343B"));
        if (parsed.TaskCandidates.Count > 0)
            badges.Add(MakeBadge("✦", $"+{parsed.TaskCandidates.Count} task", "#30343B"));
        if (completedCount > 0)
            badges.Add(MakeBadge("✓", $"{completedCount} done", "#30343B"));
        if (deletedCount > 0)
            badges.Add(MakeBadge("×", $"-{deletedCount} cal", "#4A2A2E"));
        if (movedCount > 0)
            badges.Add(MakeBadge("↗", $"moved {movedCount}", "#30343B"));
        if (editedCount > 0)
            badges.Add(MakeBadge("~", $"edited {editedCount}", "#30343B"));
        if (parsed.MemoryCandidates.Count > 0 && !isExplicitDelete)
            badges.Add(MakeBadge("🧠", "memory noted", "#30343B"));

        if (parsed.DevNoteCandidates.Count > 0)
        {
            foreach (var note in parsed.DevNoteCandidates)
            {
                await database.AddDevNoteAsync(note, "llm");
                devLog.Log($"LLM dev note: {note}", "notes");
            }
            badges.Add(MakeBadge("📋", $"+{parsed.DevNoteCandidates.Count} dev note", "#30343B"));
        }

        if (badges.Count > 0)
            assistant.ActionBadges = new System.Collections.ObjectModel.ObservableCollection<ActionBadge>(badges);

        return parsed.MemoryCandidates.Count > 0 || parsed.ScheduleCandidates.Count > 0 || parsed.TaskCandidates.Count > 0 || parsed.TaskCompleteCandidates.Count > 0 || deleteCandidates.Count > 0 || parsed.MoveCandidates.Count > 0 || movedCount > 0 || parsed.EditCandidates.Count > 0 || parsed.DevNoteCandidates.Count > 0;
    }

    private ConcreteDataExtractor? dataExtractor;

    /// <summary>
    /// Second LLM pass on every message: pulls out every concrete datum and files it as memory,
    /// tasks (optionally paired with a calendar date) and calendar events, so the user never has
    /// to add anything by hand. Duplicates of existing tasks/events are skipped.
    /// </summary>
    private async Task RunAutoExtractionAsync(string text, ChatMessage assistant, DateTimeOffset now)
    {
        if (text.Trim().Length < 6)
        {
            return;
        }

        var pass = runtimeLog.Start("extract", "pass");
        try
        {
            dataExtractor ??= new ConcreteDataExtractor(llmProvider, devLog);
            var result = await dataExtractor.ExtractAsync(text, now, MemoryItems.ToList());
            if (result.IsEmpty)
            {
                pass.Done("empty", "nothing concrete found in the message");
                return;
            }

            var source = $"auto_extract:chat_thread:{SelectedThreadId};time:{now:O}";

            var savedMemory = 0;
            foreach (var item in result.Memory)
            {
                var candidate = new MemoryCandidate(item.Key, item.Value, false, MemoryTypeForKey(item.Key));
                var validation = await TryApplyMemoryCandidateAsync(candidate, source);
                if (validation.ShouldWrite)
                {
                    savedMemory++;
                }
            }

            if (savedMemory > 0)
            {
                await LoadMemoryAsync();
                await LoadMemoryTransactionsAsync();
            }

            await LoadTasksAsync();
            await LoadEventsAsync();

            var addedTasks = 0;
            var addedEvents = 0;
            var queuedForReview = 0;

            // Adds an automatic event, but first compares loosely (symbols and spelling ignored) against what is
            // already on that day:
            //  - same event, same time (or a date-only claim): nothing to do
            //  - a manual event at a different time: the claim goes to the review list
            //  - an automatic event at a different time: the newer statement updates its time
            async Task<bool> TryAddAutoEventAsync(string title, DateTimeOffset when, string details)
            {
                var similar = CalendarEvents.FirstOrDefault(e =>
                    e.StartsAt.Date == when.Date && TextMatch.TitlesSimilar(e.Title, title));
                if (similar is not null)
                {
                    var sameTime = Math.Abs((similar.StartsAt - when).TotalMinutes) < 1;
                    var dateOnlyClaim = when.TimeOfDay == TimeSpan.Zero;
                    if (sameTime || dateOnlyClaim)
                    {
                        return false;
                    }

                    if (similar.Source == "manual")
                    {
                        await database.AddReviewItemAsync("event", similar.Id.ToString(), similar.Title,
                            similar.StartsAt.ToString("MMM d, h:mm tt"), when.ToString("O"), "auto_extract");
                        queuedForReview++;
                        return false;
                    }

                    await calendarService.UpdateEventAsync(similar.Id, similar.Title, when,
                        string.IsNullOrWhiteSpace(details) ? similar.Details : details);
                    return false;
                }

                await calendarService.AddEventAsync(title, when, details);
                return true;
            }

            foreach (var task in result.Tasks)
            {
                if (!Tasks.Any(t => TextMatch.TitlesSimilar(t.Title, task.Title)))
                {
                    await taskService.AddAsync(task.Title);
                    addedTasks++;
                }

                // Tasks tied to a date also appear on the calendar.
                if (task.Due is { } due && await TryAddAutoEventAsync(task.Title, due, "Task due"))
                {
                    addedEvents++;
                }
            }

            foreach (var ev in result.Events)
            {
                if (await TryAddAutoEventAsync(ev.Title, ev.When, ev.Details))
                {
                    addedEvents++;
                }
            }

            if (addedTasks > 0)
            {
                await LoadTasksAsync();
            }

            if (addedEvents > 0)
            {
                await LoadEventsAsync();
            }

            await LoadReviewAsync();

            var addedDevNotes = 0;
            if (result.DevNotes.Count > 0)
            {
                var existingNotes = await database.GetDevNotesAsync(null);
                foreach (var note in result.DevNotes)
                {
                    if (!existingNotes.Any(n => n.Note.Equals(note, StringComparison.OrdinalIgnoreCase)))
                    {
                        await database.AddDevNoteAsync(note, "auto");
                        addedDevNotes++;
                    }
                }
            }

            var badges = assistant.ActionBadges.ToList();
            if (addedDevNotes > 0)
                badges.Add(MakeBadge("📋", $"+{addedDevNotes} dev note", "#30343B"));
            if (savedMemory > 0)
                badges.Add(MakeBadge("🧠", $"+{savedMemory} memory", "#30343B"));
            if (addedTasks > 0)
                badges.Add(MakeBadge("✦", $"+{addedTasks} task", "#30343B"));
            if (addedEvents > 0)
                badges.Add(MakeBadge("📅", $"+{addedEvents} cal", "#30343B"));

            if (badges.Count > assistant.ActionBadges.Count)
            {
                assistant.ActionBadges = new System.Collections.ObjectModel.ObservableCollection<ActionBadge>(badges);
            }

            devLog.Log($"Auto-extract: {savedMemory} memory, {addedTasks} tasks, {addedEvents} events", "extract");
            pass.Done("ok", $"found {result.Memory.Count} memory, {result.Tasks.Count} tasks, {result.Events.Count} events, {result.DevNotes.Count} dev notes; filed {savedMemory} memory, {addedTasks} tasks, {addedEvents} events, {addedDevNotes} dev notes, {queuedForReview} events sent to review");
        }
        catch (Exception ex)
        {
            devLog.Log($"Auto-extract failed: {ex.Message}", "extract");
            pass.Done("error", ex.Message);
        }
    }

    private static ActionBadge MakeBadge(string symbol, string tooltip, string bgHex) =>
        new(symbol, tooltip, new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(bgHex)));

    private static readonly Regex ExplicitScheduleRequestRegex = new(
        @"\b(add|schedule|book|set up|put|create)\b.{0,60}\b(calendar|appointment|event|reminder)\b|\b(add|schedule|book|put)\b.{0,40}\bon\b.{0,20}\bcalendar\b|\b(add|schedule|book|put|create)\b.{0,60}\b(on|for|at)\b.{0,30}\b(am|pm|\d{1,2}(st|nd|rd|th)|\d{1,2}\/\d{1,2}|january|february|march|april|may|june|july|august|september|october|november|december|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);



    private static readonly Regex CodeFenceOpenRegex = new(
        @"^```[a-z]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ScheduleLineFallbackRegex = new(
        @"^(?<title>[^|\n]{1,120}?)\s*\|\s*(?<when>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+\-]\d{2}:\d{2})\s*(?:\|\s*(?<details>.*))?$",
        RegexOptions.CultureInvariant);

    // A bare ISO-ish timestamp. Used to reject malformed schedule lines where a date
    // leaked into the title or details field (the signature of a botched move that
    // would otherwise create a duplicate/corrupted calendar row).
    private static readonly Regex IsoTimestampRegex = new(
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}",
        RegexOptions.CultureInvariant);

}
