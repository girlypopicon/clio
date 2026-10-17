using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using CLIo.Models;
using CLIo.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CLIo.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
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
        DateTimeParsingService dateTimeParser)
    {
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
    private readonly ReminderService reminderService;
    private readonly DateTimeParsingService dateTimeParser;
    private ScopedChatSuggestion? pendingScopedChatSuggestion;
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

    [ObservableProperty]
    private bool showJumpToLatest;

    public Task Initialization { get; }

    public bool IsThreadsPanelClosed => !IsThreadsPanelOpen;

    public bool IsToolsPanelClosed => !IsToolsPanelOpen;

    public bool IsChatView => CurrentView == "Chat";

    public bool IsMemoryView => CurrentView == "Memory";

    public bool IsCalendarView => CurrentView == "Calendar";

    public bool IsTasksView => CurrentView == "Tasks";

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

        await calendarService.AddEventAsync(TaskBeingScheduled.Title, startsAt, string.Empty);
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

        await calendarService.AddEventAsync(EventTitle.Trim(), startsAt, EventDetails.Trim());
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
    }

    [RelayCommand]
    private async Task AddTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTaskTitle))
        {
            return;
        }

        await taskService.AddAsync(NewTaskTitle.Trim());
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
        await memoryService.UpsertAsync(MemoryKey.Trim(), MemoryValue.Trim(), type);
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
        if (string.IsNullOrWhiteSpace(ChatInput))
        {
            return;
        }

        await SaveContextAsync();

        var now = DateTimeOffset.Now;
        var prompt = ChatInput.Trim();
        ChatInput = string.Empty;

        if (TryParsePrefixCommand(prompt, out var commandPrefix, out var commandBody))
        {
            await HandlePrefixCommandAsync(commandPrefix, commandBody, prompt, now);
            return;
        }

        if (await TryHandleScopedChatConfirmationAsync(prompt, now))
        {
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
        var passiveCandidates = localCandidates.Where(candidate => !candidate.IsExplicitWrite).ToList();
        if (SelectedChatThread?.IsMain == true && passiveCandidates.Count > 0)
        {
            pendingMemoryConfirmationKeys = passiveCandidates
                .Select(candidate => candidate.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            MemoryToastMessage = BuildMemoryConfirmationPrompt(passiveCandidates);
            MemoryToastVisible = true;
            await LoadThreadsAsync();
            return;
        }

        var routedAgent = router.Route(string.Empty, prompt);
        MemoryStatus = $"Routed to: {routedAgent}";
        devLog.Log($"Chat routed to {routedAgent}: {prompt[..Math.Min(prompt.Length, 60)]}", "routing");

        var assistant = new ChatMessage { Role = "assistant", Content = string.Empty, Timestamp = DateTimeOffset.Now };
        ChatMessages.Add(assistant);

        await LoadEventsAsync();
        await LoadTasksAsync();
        var llmMessages = await BuildLlmMessagesAsync(prompt, now);

        await foreach (var token in llmProvider.ChatAsync(llmMessages))
        {
            assistant.Content += token;
        }

        await LogLlmExchangeAsync(llmMessages, assistant.Content);

        await ApplyStructuredMemoryAsync(assistant, prompt);
        await database.AddChatMessageAsync(SelectedThreadId, assistant.Role, assistant.Content, assistant.Timestamp);

        await TrySuggestScopedChatAsync(prompt);
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

        await foreach (var token in llmProvider.ChatAsync(llmMessages))
        {
            assistant.Content += token;
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

    private async Task<bool> TryHandleScopedChatConfirmationAsync(string prompt, DateTimeOffset timestamp)
    {
        if (pendingScopedChatSuggestion is null)
        {
            return false;
        }

        if (SelectedChatThread?.IsMain != true)
        {
            pendingScopedChatSuggestion = null;
            return false;
        }

        var normalized = prompt.Trim().ToLowerInvariant();
        var isConfirm = IsAffirmativeReply(normalized);
        var isReject = IsNegativeReply(normalized);
        if (!isConfirm && !isReject)
        {
            pendingScopedChatSuggestion = null;
            return false;
        }

        var suggestion = pendingScopedChatSuggestion;
        pendingScopedChatSuggestion = null;

        var userMessage = new ChatMessage { Role = "user", Content = prompt, Timestamp = timestamp };
        ChatMessages.Add(userMessage);
        await database.AddChatMessageAsync(SelectedThreadId, userMessage.Role, userMessage.Content, userMessage.Timestamp);

        if (isReject)
        {
            var rejectMessage = new ChatMessage { Role = "assistant", Content = "Keeping it in Main Chat.", Timestamp = DateTimeOffset.Now };
            ChatMessages.Add(rejectMessage);
            await database.AddChatMessageAsync(SelectedThreadId, rejectMessage.Role, rejectMessage.Content, rejectMessage.Timestamp);
            await LoadThreadsAsync();
            return true;
        }

        var thread = await database.AddChatThreadAsync(suggestion.Title);
        await LoadThreadsAsync();
        SelectedChatThread = ChatThreads.FirstOrDefault(item => item.Id == thread.Id) ?? SelectedChatThread;
        await LoadChatAsync();

        var createdMessage = new ChatMessage
        {
            Role = "assistant",
            Content = $"Created this scoped chat for `{suggestion.Title}`. Original topic: {suggestion.SeedPrompt}",
            Timestamp = DateTimeOffset.Now
        };
        ChatMessages.Add(createdMessage);
        await database.AddChatMessageAsync(SelectedThreadId, createdMessage.Role, createdMessage.Content, createdMessage.Timestamp);
        await LoadThreadsAsync();
        return true;
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

    private async Task TrySuggestScopedChatAsync(string prompt)
    {
        if (SelectedChatThread?.IsMain != true || pendingScopedChatSuggestion is not null || !ShouldSuggestScopedChat(prompt))
        {
            return;
        }

        var title = BuildScopedChatTitle(prompt);
        pendingScopedChatSuggestion = new ScopedChatSuggestion(title, prompt);

        var message = new ChatMessage
        {
            Role = "assistant",
            Content = $"This looks like a focused topic. Want me to create a separate chat for `{title}`?",
            Timestamp = DateTimeOffset.Now
        };
        ChatMessages.Add(message);
        await database.AddChatMessageAsync(SelectedThreadId, message.Role, message.Content, message.Timestamp);
    }

    private static readonly Regex CalendarRelatedRegex = new(
        @"\b(calendar|schedule|appointment|appt|event|events|reminder|reminders|meeting|pedicure|haircut|dentist|today|tomorrow|tonight|monday|tuesday|wednesday|thursday|friday|saturday|sunday|week|month|\d{1,2}\s*(am|pm)|\d{1,2}:\d{2}|\d{1,2}\/\d{1,2})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool ShouldSuggestScopedChat(string prompt)
    {
        var normalized = prompt.Trim().ToLowerInvariant();
        if (normalized.Length < 80 || IsAffirmativeReply(normalized) || IsNegativeReply(normalized))
        {
            return false;
        }

        // Anything calendar/date/appointment/time related belongs in Main Chat - never split it out.
        if (CalendarRelatedRegex.IsMatch(normalized)
            || IsExplicitDeleteRequest(prompt)
            || IsExplicitScheduleRequest(prompt)
            || InferCalendarDirectiveFromPrompt(prompt) is not null)
        {
            return false;
        }

        return Regex.IsMatch(
            normalized,
            @"\b(project|plan|planning|build|app|idea|issue|problem|trip|move|job|work|school|budget|health|fitness|workout|diet|house|apartment|clio|research|strategy|design|feature|deadline)\b|\b(help me with|work on|figure out|think through|make a plan)\b",
            RegexOptions.CultureInvariant);
    }

    private static string BuildScopedChatTitle(string prompt)
    {
        var title = Regex.Replace(prompt, @"\s+", " ").Trim();
        title = Regex.Replace(
            title,
            @"^(can you|could you|please|help me|help me with|i need to|i want to|let'?s|lets)\s+",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        title = title.Trim(' ', '.', ',', ';', ':', '!', '?', '"', '\'');
        if (title.Length > 48)
        {
            title = title[..45].TrimEnd() + "...";
        }

        return string.IsNullOrWhiteSpace(title)
            ? "Focused Chat"
            : char.ToUpperInvariant(title[0]) + title[1..];
    }

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
            SelfContext = selfContext
        });

        var context = new StringBuilder(systemPrompt);

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

    private static ActionBadge MakeBadge(string symbol, string tooltip, string bgHex) =>
        new(symbol, tooltip, new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(bgHex)));

    private static readonly Regex ExplicitScheduleRequestRegex = new(
        @"\b(add|schedule|book|set up|put|create)\b.{0,60}\b(calendar|appointment|event|reminder)\b|\b(add|schedule|book|put)\b.{0,40}\bon\b.{0,20}\bcalendar\b|\b(add|schedule|book|put|create)\b.{0,60}\b(on|for|at)\b.{0,30}\b(am|pm|\d{1,2}(st|nd|rd|th)|\d{1,2}\/\d{1,2}|january|february|march|april|may|june|july|august|september|october|november|december|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private List<CalendarEvent> FindMatchingEvents(string title, string? when)
    {
        DateTime? whenDate = null;
        TimeSpan? whenTime = null;
        if (DateTime.TryParse(when, out var parsedWhen))
        {
            whenDate = parsedWhen.Date;
            if (parsedWhen.TimeOfDay != TimeSpan.Zero)
            {
                whenTime = parsedWhen.TimeOfDay;
            }
        }

        var titleMatches = CalendarEvents
            .Where(e => e.Title.Contains(title, StringComparison.OrdinalIgnoreCase)
                || title.Contains(e.Title, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var matches = titleMatches
            .Where(e => whenDate is null || e.StartsAt.Date == whenDate.Value)
            .ToList();

        if (whenTime is not null && matches.Count > 1)
        {
            var exact = matches.Where(e => e.StartsAt.TimeOfDay == whenTime.Value).ToList();
            if (exact.Count > 0)
            {
                matches = exact;
            }
        }

        if (matches.Count == 0 && whenDate is not null && titleMatches.Count == 1)
        {
            // The given date didn't match, but there's only one event with this title overall -
            // treat it as the intended target rather than reporting "not found".
            matches = titleMatches;
        }

        return matches;
    }

    private static readonly Regex EventIdReferenceRegex = new(
        @"^(?:#|id[:=]?\s*)(\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Resolves the target event(s) for a delete/move/edit skill. If the match field is an
    // #id reference (which CLIo is told to prefer), resolve directly by primary key for an
    // exact, unambiguous hit; otherwise fall back to fuzzy title/date matching.
    private List<CalendarEvent> ResolveTargetEvents(string titleOrId, string? when)
    {
        var idMatch = EventIdReferenceRegex.Match(titleOrId.Trim());
        if (idMatch.Success && int.TryParse(idMatch.Groups[1].Value, out var id))
        {
            return CalendarEvents.Where(e => e.Id == id).ToList();
        }

        return FindMatchingEvents(titleOrId, when);
    }

    private static bool IsExplicitScheduleRequest(string prompt)
    {
        return ExplicitScheduleRequestRegex.IsMatch(prompt);
    }

    private static readonly Regex ExplicitDeleteRequestRegex = new(
        @"\b(delete|cancel|remove|get rid of)\b.{0,60}\b(calendar|appointment|event|reminder|meeting|item|items|thing|things|entry|entries)\b" +
        @"|\b(delete|cancel|remove|get rid of)\b.{0,40}\bfrom\b.{0,20}\b(calendar|schedule|week|month)\b" +
        @"|\b(delete|cancel|remove|get rid of)\b.{0,30}\b(on|off|of)\b.{0,20}\b(my|the)\b.{0,10}\b(calendar|schedule|week|month)\b" +
        @"|\b(delete|cancel|remove|get rid of)\b.{0,5}\b(my|the)\b.{1,40}\S",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DeleteStopWordsRegex = new(
        @"\b(please|delete|cancel|remove|get|rid|of|my|the|an?|from|calendar|appointment|event|reminder|meeting|schedule|for)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsExplicitDeleteRequest(string prompt)
    {
        return ExplicitDeleteRequestRegex.IsMatch(prompt);
    }

    private static string ExtractDeleteTitleHint(string prompt)
    {
        var cleaned = DeleteStopWordsRegex.Replace(prompt, " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    // Move-intent verb set: reschedule, delay/postpone/defer, advance/bring-forward, numeric-shift
    private static readonly Regex ExplicitMoveRequestRegex = new(
        @"\b(?:move|reschedule|resked|push(?:\s+back)?|bump|shift|change"
        + @"|postpone|delay|defer|put\s+off"
        + @"|advance|bring\s+(?:forward|ahead)|pull\s+(?:forward|up|ahead)|move\s+(?:up|earlier|ahead))\b"
        + @".{0,60}\b(?:to|on|for|until|till|by|back|forward|ahead)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MoveStopWordsRegex = new(
        @"\b(?:please|plz|pls|can|could|you|lets|let"
        + @"|move|reschedule|resked|push|bump|shift|change|postpone|delay|defer|put|off"
        + @"|advance|bring|pull|up|earlier"
        + @"|the|my|a|an|back|forward|ahead|to|on|for|until|till|over|by"
        + @"|this|that|next|coming|last|same|time"
        + @"|appointment|appt|event|meeting|reminder)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsExplicitMoveRequest(string prompt) => ExplicitMoveRequestRegex.IsMatch(prompt);

    private static string ExtractMoveTitleHint(string prompt)
    {
        var cleaned = MoveStopWordsRegex.Replace(prompt, " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    // ── Date-parsing vocabulary ──────────────────────────────────────────────

    private static readonly string[] MonthNames =
        { "january", "february", "march", "april", "may", "june",
          "july", "august", "september", "october", "november", "december" };

    // ISO: 2026-06-24
    private static readonly Regex IsoDateRegex = new(
        @"\b(\d{4})-(\d{1,2})-(\d{1,2})\b",
        RegexOptions.CultureInvariant);

    // Month-first: "June 24", "Jun 24th"
    private static readonly Regex MonthDayRegex = new(
        @"\b(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?"
        + @"|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\.?\s+(\d{1,2})(?:st|nd|rd|th)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Day-first: "24 June", "24th June", "24th of June"
    private static readonly Regex DayMonthRegex = new(
        @"\b(\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?"
        + @"(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?"
        + @"|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Word-ordinal of month: "first of June", "twenty-fourth of next month"
    private static readonly Regex WordOrdinalOfMonthRegex = new(
        @"\b(?:the\s+)?(first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth"
        + @"|eleventh|twelfth|thirteenth|fourteenth|fifteenth|sixteenth|seventeenth"
        + @"|eighteenth|nineteenth|twentieth|twenty[- ]first|twenty[- ]second|twenty[- ]third"
        + @"|twenty[- ]fourth|twenty[- ]fifth|twenty[- ]sixth|twenty[- ]seventh|twenty[- ]eighth"
        + @"|twenty[- ]ninth|thirtieth|thirty[- ]first)"
        + @"\s+of\s+"
        + @"(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?"
        + @"|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?|next\s+month|this\s+month)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Numeric: "6/24", "06/24/26"
    private static readonly Regex NumericDateRegex = new(
        @"\b(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?\b",
        RegexOptions.CultureInvariant);

    // Bare weekday or today/tomorrow/yesterday (no modifier)
    private static readonly Regex DayOfWeekRegex = new(
        @"\b(today|tomorrow|yesterday|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Weekday with modifier: "next monday", "this friday", "coming wednesday", "last tuesday"
    private static readonly Regex WeekdayWithModifierRegex = new(
        @"\b(next|this|coming|last)\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "day after tomorrow" / "overmorrow"
    private static readonly Regex DayAfterTomorrowRegex = new(
        @"\b(?:day\s+after\s+tomorrow|overmorrow)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Bare ordinal: "the 24th", "5th" (checked last — lowest specificity)
    private static readonly Regex DayOfMonthRegex = new(
        @"\b(?:the\s+)?(\d{1,2})(?:st|nd|rd|th)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Last-resort: bare number 1–31 as a day, not followed by a time/duration unit
    private static readonly Regex BareDayNumberRegex = new(
        @"(?<!\d)\b([1-9]|[12]\d|3[01])\b(?!\s*(?:st|nd|rd|th|days?|weeks?|months?|years?|hrs?|hours?|mins?|minutes?|am|pm|:\d{2}))(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Relative-from-now: "in 3 days", "a week from now", "3 days later"
    private static readonly Regex InXDaysRegex = new(
        @"\bin\s+(\d+|a|an|one|two|three|four|five|six|seven)\s+(days?|weeks?)\b"
        + @"|\b(\d+|a|an|one|two|three|four|five|six|seven)\s+(days?|weeks?)"
        + @"\s+(?:from\s+(?:now|today)|later|hence|out)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Relative delta for moves: "by 3 days", "back a week", "2 weeks forward", "3 days earlier"
    private static readonly Regex RelativeOffsetRegex = new(
        @"\b(?:by|back|forward|ahead)\s+(\d+|a|an|one|two|three|four|five|six|seven)\s+(days?|weeks?)\b"
        + @"|\b(\d+|a|an|one|two|three|four|five|six|seven)\s+(days?|weeks?)"
        + @"\s+(?:back|forward|ahead|earlier|later)\b"
        + @"|\bby\s+a\s+(day|week)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Period boundaries: "end of month", "start of next month", "beginning of the week"
    private static readonly Regex PeriodBoundaryRegex = new(
        @"\b(start|beginning|begin|end)\s+of\s+(?:the\s+)?(?:(next|this|last)\s+)?(month|week|year)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ── ExtractFlexibleDate ──────────────────────────────────────────────────
    // Pulls a concrete target date from free text. Priority order:
    //   ISO → day-after-tomorrow → month+day → day+month → word-ordinal-of-month
    //   → numeric → relative-from-now → period-boundary → weekday+modifier
    //   → bare weekday/today/tomorrow → bare day ordinal
    private static DateTime? ExtractFlexibleDate(string text, out string textWithoutDate)
    {
        textWithoutDate = CleanRemainder(text);
        var today = DateTime.Today;

        // 1. ISO date: 2026-06-24
        var iso = IsoDateRegex.Match(text);
        if (iso.Success
            && int.TryParse(iso.Groups[1].Value, out var iy)
            && int.TryParse(iso.Groups[2].Value, out var im)
            && int.TryParse(iso.Groups[3].Value, out var idd))
        {
            var d = SafeDate(iy, im, idd);
            if (d is not null) { textWithoutDate = CleanRemainder(text.Remove(iso.Index, iso.Length)); return d; }
        }

        // 2. "day after tomorrow" / "overmorrow"
        var dat = DayAfterTomorrowRegex.Match(text);
        if (dat.Success)
        {
            textWithoutDate = CleanRemainder(text.Remove(dat.Index, dat.Length));
            return today.AddDays(2);
        }

        // 3. Month-first: "June 24", "Jun 24th"
        var mdf = MonthDayRegex.Match(text);
        if (mdf.Success && int.TryParse(mdf.Groups[2].Value, out var mdfDay))
        {
            var month = MonthFromToken(mdf.Groups[1].Value);
            var d = SafeDate(today.Year, month, mdfDay);
            if (d is not null)
            {
                if (d.Value < today.AddDays(-1)) d = SafeDate(today.Year + 1, month, mdfDay);
                textWithoutDate = CleanRemainder(text.Remove(mdf.Index, mdf.Length));
                return d;
            }
        }

        // 4. Day-first: "24 June", "24th of June"
        var dmf = DayMonthRegex.Match(text);
        if (dmf.Success && int.TryParse(dmf.Groups[1].Value, out var dmfDay))
        {
            var month = MonthFromToken(dmf.Groups[2].Value);
            var d = SafeDate(today.Year, month, dmfDay);
            if (d is not null)
            {
                if (d.Value < today.AddDays(-1)) d = SafeDate(today.Year + 1, month, dmfDay);
                textWithoutDate = CleanRemainder(text.Remove(dmf.Index, dmf.Length));
                return d;
            }
        }

        // 5. Word-ordinal of month: "first of June", "twenty-fourth of next month"
        var woom = WordOrdinalOfMonthRegex.Match(text);
        if (woom.Success)
        {
            var day = OrdinalWordToNumber(woom.Groups[1].Value);
            var monthToken = woom.Groups[2].Value.ToLowerInvariant();
            int month, year = today.Year;
            if (monthToken.Contains("next"))
            {
                var nm2 = today.AddMonths(1);
                month = nm2.Month; year = nm2.Year;
            }
            else if (monthToken.Contains("this"))
            {
                month = today.Month;
            }
            else
            {
                month = MonthFromToken(monthToken);
            }
            if (day > 0 && month > 0)
            {
                var d = SafeDate(year, month, day);
                if (d is not null)
                {
                    if (d.Value < today.AddDays(-1) && !monthToken.Contains("next") && !monthToken.Contains("this"))
                        d = SafeDate(today.Year + 1, month, day);
                    textWithoutDate = CleanRemainder(text.Remove(woom.Index, woom.Length));
                    return d;
                }
            }
        }

        // 6. Numeric date: "6/24", "06/24/26"
        var nd = NumericDateRegex.Match(text);
        if (nd.Success
            && int.TryParse(nd.Groups[1].Value, out var nm)
            && int.TryParse(nd.Groups[2].Value, out var ndd))
        {
            var year = today.Year;
            if (nd.Groups[3].Success && int.TryParse(nd.Groups[3].Value, out var ny))
                year = ny < 100 ? 2000 + ny : ny;
            var d = SafeDate(year, nm, ndd);
            if (d is not null)
            {
                if (!nd.Groups[3].Success && d.Value < today.AddDays(-1))
                    d = SafeDate(year + 1, nm, ndd);
                textWithoutDate = CleanRemainder(text.Remove(nd.Index, nd.Length));
                return d;
            }
        }

        // 7. Relative-from-now: "in 3 days", "a week from now", "3 days later"
        var inx = InXDaysRegex.Match(text);
        if (inx.Success)
        {
            var cStr = inx.Groups[1].Success && !string.IsNullOrEmpty(inx.Groups[1].Value)
                ? inx.Groups[1].Value : inx.Groups[3].Value;
            var uStr = inx.Groups[2].Success && !string.IsNullOrEmpty(inx.Groups[2].Value)
                ? inx.Groups[2].Value : inx.Groups[4].Value;
            var count = WordToNumber(cStr);
            if (count > 0)
            {
                var days = uStr.StartsWith("week", StringComparison.OrdinalIgnoreCase) ? count * 7 : count;
                textWithoutDate = CleanRemainder(text.Remove(inx.Index, inx.Length));
                return today.AddDays(days);
            }
        }

        // 8. Period boundaries: "end of month", "start of next month", "beginning of week"
        var pb = PeriodBoundaryRegex.Match(text);
        if (pb.Success)
        {
            var boundary = pb.Groups[1].Value.ToLowerInvariant();
            var modifier = pb.Groups[2].Success ? pb.Groups[2].Value.ToLowerInvariant() : "this";
            var period = pb.Groups[3].Value.ToLowerInvariant();
            var isEnd = boundary == "end";
            var isNext = modifier == "next";
            DateTime? d = period switch
            {
                "week" => isEnd ? EndOfWeek(today) : StartOfWeek(today),
                "month" when isEnd && isNext  => EndOfMonth(today.AddMonths(1)),
                "month" when isEnd            => EndOfMonth(today),
                "month" when isNext           => new DateTime(today.AddMonths(1).Year, today.AddMonths(1).Month, 1),
                "month"                       => new DateTime(today.Year, today.Month, 1),
                "year"  when isEnd && isNext  => new DateTime(today.Year + 1, 12, 31),
                "year"  when isEnd            => new DateTime(today.Year, 12, 31),
                "year"  when isNext           => new DateTime(today.Year + 1, 1, 1),
                "year"                        => new DateTime(today.Year, 1, 1),
                _ => null
            };
            if (d is not null) { textWithoutDate = CleanRemainder(text.Remove(pb.Index, pb.Length)); return d; }
        }

        // 9. Weekday with modifier: "next monday", "this friday", "last wednesday"
        var wm = WeekdayWithModifierRegex.Match(text);
        if (wm.Success)
        {
            var modifier = wm.Groups[1].Value.ToLowerInvariant();
            var dayName = wm.Groups[2].Value.ToLowerInvariant();
            var targetDow = (DayOfWeek)Array.IndexOf(
                new[] { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" }, dayName);
            var diff = ((int)targetDow - (int)today.DayOfWeek + 7) % 7;
            var daysToNextMonday = (1 - (int)today.DayOfWeek + 7) % 7;
            if (daysToNextMonday == 0) daysToNextMonday = 7;
            var daysFromMondayToTarget = ((int)targetDow - 1 + 7) % 7;
            DateTime d = modifier switch
            {
                // "next [day]" = that weekday in next calendar week (Mon–Sun)
                "next" => today.AddDays(daysToNextMonday + daysFromMondayToTarget),
                // "last [day]" = most recent past occurrence
                "last" => today.AddDays(-((int)today.DayOfWeek - (int)targetDow + 7) % 7 is 0 ? 7 : (int)today.DayOfWeek - (int)targetDow),
                // "this" / "coming" = next upcoming occurrence
                _ => today.AddDays(diff == 0 ? 7 : diff)
            };
            textWithoutDate = CleanRemainder(text.Remove(wm.Index, wm.Length));
            return d;
        }

        // 10. Bare weekday / today / tomorrow / yesterday
        if (DayOfWeekRegex.IsMatch(text))
        {
            var word = DayOfWeekRegex.Match(text).Value.ToLowerInvariant();
            DateTime? d = word switch
            {
                "today"     => today,
                "tomorrow"  => today.AddDays(1),
                "yesterday" => today.AddDays(-1),
                _ => null
            };
            if (d is null)
            {
                var targetDow = (DayOfWeek)Array.IndexOf(
                    new[] { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" }, word);
                var diff = ((int)targetDow - (int)today.DayOfWeek + 7) % 7;
                d = today.AddDays(diff == 0 ? 7 : diff);
            }
            var wo = DayOfWeekRegex.Match(text);
            textWithoutDate = CleanRemainder(text.Remove(wo.Index, wo.Length));
            return d;
        }

        // 11. Bare day ordinal: "the 24th", "5th"
        var dom = DayOfMonthRegex.Match(text);
        if (dom.Success && int.TryParse(dom.Groups[1].Value, out var domDay))
        {
            var d = SafeDate(today.Year, today.Month, domDay);
            if (d is null || d.Value < today)
            {
                var next = today.AddMonths(1);
                d = SafeDate(next.Year, next.Month, domDay);
            }
            if (d is not null) { textWithoutDate = CleanRemainder(text.Remove(dom.Index, dom.Length)); return d; }
        }

        // 12. Last resort: bare number 1–31 as day of month (e.g. "reschedule dentist for 24")
        var bdn = BareDayNumberRegex.Match(text);
        if (bdn.Success && int.TryParse(bdn.Groups[1].Value, out var bdDay))
        {
            var d = SafeDate(today.Year, today.Month, bdDay);
            if (d is null || d.Value < today)
            {
                var next = today.AddMonths(1);
                d = SafeDate(next.Year, next.Month, bdDay);
            }
            if (d is not null) { textWithoutDate = CleanRemainder(text.Remove(bdn.Index, bdn.Length)); return d; }
        }

        return null;
    }

    // Extracts a signed time delta for relative moves ("by a week", "back 3 days", "2 days earlier").
    // Returns false when no delta phrase is present.
    private static bool TryExtractRelativeOffset(string text, out string textWithoutOffset, out TimeSpan offset)
    {
        textWithoutOffset = CleanRemainder(text);
        offset = TimeSpan.Zero;
        var m = RelativeOffsetRegex.Match(text);
        if (!m.Success) return false;

        string countStr, unitStr;
        if (m.Groups[1].Success && !string.IsNullOrEmpty(m.Groups[1].Value))
        {
            countStr = m.Groups[1].Value; unitStr = m.Groups[2].Value;
        }
        else if (m.Groups[3].Success && !string.IsNullOrEmpty(m.Groups[3].Value))
        {
            countStr = m.Groups[3].Value; unitStr = m.Groups[4].Value;
        }
        else if (m.Groups[5].Success)
        {
            countStr = "1"; unitStr = m.Groups[5].Value;
        }
        else return false;

        var count = WordToNumber(countStr);
        if (count <= 0) return false;

        var days = unitStr.StartsWith("week", StringComparison.OrdinalIgnoreCase) ? count * 7 : count;
        var backward = Regex.IsMatch(m.Value, @"\b(?:back|earlier)\b", RegexOptions.IgnoreCase);
        offset = TimeSpan.FromDays(backward ? -days : days);
        textWithoutOffset = CleanRemainder(text.Remove(m.Index, m.Length));
        return true;
    }

    private static DateTime? SafeDate(int year, int month, int day)
    {
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return null;
        return new DateTime(year, month, day);
    }

    private static int MonthFromToken(string token)
    {
        token = token.TrimEnd('.').ToLowerInvariant();
        for (var i = 0; i < MonthNames.Length; i++)
            if (MonthNames[i].StartsWith(token, StringComparison.Ordinal)) return i + 1;
        return 0;
    }

    private static int WordToNumber(string word) => word.ToLowerInvariant() switch
    {
        "a" or "an" or "one" => 1, "two" => 2, "three" => 3, "four" => 4,
        "five" => 5, "six" => 6, "seven" => 7,
        _ => int.TryParse(word, out var n) ? n : 0
    };

    private static int OrdinalWordToNumber(string word)
    {
        var w = word.ToLowerInvariant().Replace("-", " ");
        return w switch
        {
            "first" => 1, "second" => 2, "third" => 3, "fourth" => 4, "fifth" => 5,
            "sixth" => 6, "seventh" => 7, "eighth" => 8, "ninth" => 9, "tenth" => 10,
            "eleventh" => 11, "twelfth" => 12, "thirteenth" => 13, "fourteenth" => 14,
            "fifteenth" => 15, "sixteenth" => 16, "seventeenth" => 17, "eighteenth" => 18,
            "nineteenth" => 19, "twentieth" => 20,
            "twenty first" => 21, "twenty second" => 22, "twenty third" => 23,
            "twenty fourth" => 24, "twenty fifth" => 25, "twenty sixth" => 26,
            "twenty seventh" => 27, "twenty eighth" => 28, "twenty ninth" => 29,
            "thirtieth" => 30, "thirty first" => 31,
            _ => 0
        };
    }

    private static DateTime EndOfWeek(DateTime d)
    {
        // Sunday = end of week
        var diff = (7 - (int)d.DayOfWeek) % 7;
        return d.AddDays(diff == 0 ? 7 : diff);
    }

    private static DateTime StartOfWeek(DateTime d)
    {
        // Monday = start of week
        var diff = ((int)d.DayOfWeek - 1 + 7) % 7;
        return d.AddDays(-diff);
    }

    private static DateTime EndOfMonth(DateTime d) =>
        new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    private static string CleanRemainder(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    // Legacy shim: resolves weekday names / today / tomorrow — called only from ExtractFlexibleDate step 10
    private static string? ExtractDateHint(string titleHint, out string titleWithoutDate)
    {
        titleWithoutDate = titleHint;
        return null; // superseded by ExtractFlexibleDate; kept to avoid orphan references
    }

    private static readonly Regex CalendarRequestRegex = new(
        @"\b(?:next|this|coming)?\s*(?:week|month|two\s*weeks?|fortnight)\b.{0,20}\b(?:look|looks|looking|going on|happening|schedule|calendar|on the calendar|view)" +
        @"|\b(?:show|see|view|tell)\b.{0,30}\b(?:calendar|schedule|week|month)" +
        @"|\b(?:week|month|two\s*weeks?|fortnight)\b.{0,10}\bview\b" +
        @"|\bschedule\b.{0,10}\b(?:look|looks)" +
        @"|\bcalendar\b.{0,10}\bfor\b" +
        @"|\b(?:on|in)\s+(?:my\s+)?calendar\b.{0,25}\b(?:week|month|today|tomorrow|two\s*weeks?|fortnight)?\b" +
        @"|\bcalendar\b.{0,25}\b(?:week|month|today|tomorrow|two\s*weeks?|fortnight)\b" +
        @"|\b(?:what'?s|whats|what\s+is|anything|what)\b.{0,30}\b(?:calendar|schedule)\b" +
        @"|\b(?:have|got|going on|happening)\b.{0,20}\b(?:this|next|coming)?\s*(?:week|month|two\s*weeks?|fortnight)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static CalendarDirective? InferCalendarDirectiveFromPrompt(string prompt)
    {
        if (!CalendarRequestRegex.IsMatch(prompt))
        {
            return null;
        }

        string mode;
        if (Regex.IsMatch(prompt, @"two\s*weeks?|fortnight|14\s*days?", RegexOptions.IgnoreCase))
        {
            mode = "TwoWeek";
        }
        else if (Regex.IsMatch(prompt, @"\bmonth\b", RegexOptions.IgnoreCase))
        {
            mode = "Month";
        }
        else
        {
            mode = "Week";
        }

        // "next two weeks" means starting now (this week + next week), not shifted 14 days out -
        // only "next week"/"next month" anchor the view forward by one period.
        DateTime? anchor = null;
        if (mode == "Month" && Regex.IsMatch(prompt, @"\bnext\s+month\b", RegexOptions.IgnoreCase))
        {
            anchor = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
        }
        else if (mode == "Week" && Regex.IsMatch(prompt, @"\bnext\s+week\b", RegexOptions.IgnoreCase))
        {
            anchor = DateTime.Today.AddDays(7);
        }

        return new CalendarDirective(mode, anchor);
    }

    private static readonly Regex AsciiCalendarBlockRegex = new(
        @"```[\s\S]*?```",
        RegexOptions.Compiled);

    private static readonly Regex AsciiCalendarLineRegex = new(
        @"\b(?:Sun|Mon|Tue|Wed|Thu|Fri|Sat)\s+\d{1,2}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AsciiCalendarHeaderLineRegex = new(
        @"(?m)^\s*(?:Week|Month) of [A-Za-z]+ \d{1,2}.*$\n?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string StripAsciiCalendarBlocks(string content)
    {
        var result = AsciiCalendarBlockRegex.Replace(content, match => AsciiCalendarLineRegex.IsMatch(match.Value) ? string.Empty : match.Value);
        result = AsciiCalendarHeaderLineRegex.Replace(result, string.Empty);
        return Regex.Replace(result, @"\n{3,}", "\n\n").Trim();
    }

    private void ApplyCalendarDirective(ChatMessage assistant, CalendarDirective directive)
    {
        var anchor = directive.Anchor ?? DateTime.Today;
        var rows = directive.Mode switch
        {
            "Week" => 1,
            "TwoWeek" => 2,
            _ => 6
        };

        DateTime gridStart;
        if (directive.Mode == "Month")
        {
            var firstOfMonth = new DateTime(anchor.Year, anchor.Month, 1);
            gridStart = firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);
        }
        else
        {
            gridStart = anchor.AddDays(-(int)anchor.DayOfWeek);
        }

        var totalDays = rows * 7;
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
                IsCurrentMonth = directive.Mode != "Month" || date.Month == anchor.Month,
                IsToday = date == DateTime.Today,
                IsSelected = date == anchor,
                HasEvents = dayEvents.Count > 0,
                Events = dayEvents
            });
        }

        var end = gridStart.AddDays(totalDays - 1);
        assistant.CalendarWidgetLabel = directive.Mode switch
        {
            "Month" => anchor.ToString("MMMM yyyy"),
            _ => gridStart.Year == end.Year ? $"{gridStart:MMM d} – {end:MMM d, yyyy}" : $"{gridStart:MMM d, yyyy} – {end:MMM d, yyyy}"
        };
        assistant.CalendarWidgetDays = new ObservableCollection<CalendarDayCell>(days);

        var rangeLabel = directive.Mode switch
        {
            "Week" => "this week",
            "TwoWeek" => "the next two weeks",
            _ => "this month"
        };

        var eventCount = days.Sum(day => day.Events.Count);
        assistant.Content = eventCount == 0
            ? $"Nothing on the calendar for {rangeLabel}."
            : $"Here's {rangeLabel} - {eventCount} item(s) on the calendar.";
    }

    private async Task<IReadOnlyList<MemoryCandidate>> ApplyMemoryCandidatesAsync(IReadOnlyList<MemoryCandidate> candidates, string source)
    {
        var pending = new List<MemoryCandidate>();
        foreach (var candidate in candidates)
        {
            var validation = await TryApplyMemoryCandidateAsync(candidate, source);
            if (validation.Status == "pending_review")
            {
                pending.Add(candidate);
            }
        }

        await LoadMemoryAsync();
        await LoadMemoryTransactionsAsync();
        return pending;
    }

    private async Task<IReadOnlyList<MemoryCandidate>> ApplyLocalMemoryExtractionAsync(string userPrompt, DateTimeOffset timestamp)
    {
        var candidates = ExtractLocalMemoryCandidates(userPrompt);
        if (candidates.Count == 0)
        {
            return candidates;
        }

        await ApplyMemoryCandidatesAsync(candidates, $"local_capture:chat_thread:{SelectedThreadId};time:{timestamp:O}");
        return candidates;
    }

    private void ShowMemoryConfirmationToast(IReadOnlyList<MemoryCandidate> candidates)
    {
        pendingMemoryConfirmationKeys = candidates
            .Select(candidate => candidate.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        MemoryToastMessage = BuildMemoryConfirmationPrompt(candidates);
        MemoryToastVisible = true;
    }

    private void QueueOrShowMemoryConfirmation(IReadOnlyList<MemoryCandidate> candidates)
    {
        if (!MemoryToastVisible)
        {
            ShowMemoryConfirmationToast(candidates);
        }
        else
        {
            pendingMemoryCandidates = candidates.ToList();
        }
    }

    private static string BuildMemoryConfirmationPrompt(IReadOnlyList<MemoryCandidate> candidates)
    {
        if (candidates.Count == 1)
        {
            var candidate = candidates[0];
            return $"Should I store `{candidate.Key}` as `{candidate.Value}`?";
        }

        var builder = new StringBuilder("Should I store these memories?\n");
        foreach (var candidate in candidates)
        {
            builder.AppendLine($"- `{candidate.Key}`: {candidate.Value}");
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<MemoryCandidate> ExtractLocalMemoryCandidates(string text)
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

    private static void AddExplicitKeyValueMatches(List<MemoryCandidate> candidates, string text)
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

    private static void AddNaturalUpdateMatches(List<MemoryCandidate> candidates, string text)
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

    private static void AddMatch(List<MemoryCandidate> candidates, string text, string key, string pattern)
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

    private static void AddPreferenceMatch(List<MemoryCandidate> candidates, string text, string pattern)
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

    private static void AddMappedCandidate(List<MemoryCandidate> candidates, string field, string value, bool isExplicitWrite)
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

    private static void AddCandidate(List<MemoryCandidate> candidates, string key, string value, bool isExplicitWrite)
    {
        if (!isExplicitWrite && candidates.Any(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase) && candidate.IsExplicitWrite))
        {
            return;
        }

        candidates.RemoveAll(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        candidates.Add(new MemoryCandidate(key, value, isExplicitWrite, MemoryTypeForKey(key)));
    }

    private static string MemoryTypeForKey(string key)
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

    private static string? MapMemoryField(string normalized)
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

        // If it already looks like a colon-delimited key, validate the category
        if (normalized.Contains(':'))
        {
            var category = normalized.Split(':')[0];
            return ValidCategories.Contains(category) ? normalized : null;
        }

        return null;
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

    private static bool IsValidMemoryKeyName(string key)
    {
        return Regex.IsMatch(key, @"^[a-z][a-z0-9_]{0,29}(:[a-z][a-z0-9_]{0,29}){0,4}$", RegexOptions.CultureInvariant)
            && !key.Contains("__", StringComparison.Ordinal)
            && !string.Equals(key, "key", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(key, "field", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<MemoryValidationResult> TryApplyMemoryCandidateAsync(MemoryCandidate candidate, string source)
    {
        var validation = await ValidateMemoryCandidateAsync(candidate);
        var transactionId = await database.AddMemoryTransactionAsync(
            "write",
            candidate.Key,
            candidate.Value,
            validation.Status,
            source,
            validation.Error,
            candidate.Type);

        if (!validation.ShouldWrite)
        {
            return validation;
        }

        try
        {
            await database.UpsertMemoryAsync(candidate.Key, candidate.Value, candidate.Type);
            await database.UpdateMemoryTransactionStatusAsync(transactionId, "success");
        }
        catch (Exception ex)
        {
            await database.UpdateMemoryTransactionStatusAsync(transactionId, "failed", ex.Message);
        }

        return validation;
    }

    private async Task<MemoryValidationResult> ValidateMemoryCandidateAsync(MemoryCandidate candidate)
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

        var existingValue = await database.GetMemoryValueAsync(candidate.Key);
        if (string.Equals(existingValue, candidate.Value, StringComparison.Ordinal))
        {
            return new MemoryValidationResult("success", "Already stored.", ShouldWrite: false);
        }

        if (!string.IsNullOrWhiteSpace(existingValue) && ImmutableMemoryKeys.Contains(candidate.Key) && !candidate.IsExplicitWrite)
        {
            return new MemoryValidationResult("pending_review", $"Immutable field already set to '{existingValue}'. Confirm before updating.", ShouldWrite: false);
        }

        if (!string.IsNullOrWhiteSpace(existingValue) && !candidate.IsExplicitWrite)
        {
            return new MemoryValidationResult("pending_review", $"Existing value is '{existingValue}'. Confirm before updating.", ShouldWrite: false);
        }

        if (SelectedChatThread?.IsMain != true)
        {
            return new MemoryValidationResult("pending_review", "Detected in a scoped chat. Confirm from Main Chat or Memory before saving.", ShouldWrite: false);
        }

        if (!candidate.IsExplicitWrite)
        {
            return new MemoryValidationResult("pending_review", "Detected possible memory. Awaiting confirmation.", ShouldWrite: false);
        }

        return new MemoryValidationResult("pending_review", string.Empty, ShouldWrite: true);
    }

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

    private static ParsedAssistantResponse ParseAssistantResponse(string content)
    {
        var visibleLines = new List<string>();
        var candidates = new List<MemoryCandidate>();
        var scheduleCandidates = new List<ScheduleCandidate>();
        var taskCandidates = new List<TaskCandidate>();
        var taskCompleteCandidates = new List<TaskCompleteCandidate>();
        var deleteCandidates = new List<DeleteCandidate>();
        var moveCandidates = new List<MoveCandidate>();
        var editCandidates = new List<EditCandidate>();
        var devNoteCandidates = new List<string>();
        var structuredBlock = string.Empty;
        CalendarDirective? calendarDirective = null;
        var insideCodeFence = false;

        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (IsLeakedTimestampLine(trimmed))
            {
                continue;
            }

            // Strip code fences — unwrap content, skip the fence lines themselves.
            if (string.IsNullOrEmpty(structuredBlock) && CodeFenceOpenRegex.IsMatch(trimmed))
            {
                insideCodeFence = !insideCodeFence;
                continue;
            }

            if (string.IsNullOrEmpty(structuredBlock) && TryParseCalendarDirective(trimmed, out var directive))
            {
                calendarDirective = directive;
                continue;
            }

            if (IsStructuredBlockHeader(trimmed))
            {
                structuredBlock = NormalizeStructuredBlockHeader(trimmed);
                continue;
            }

            if (!string.IsNullOrEmpty(structuredBlock))
            {
                if (string.Equals(structuredBlock, "MEMORY CANDIDATES", StringComparison.OrdinalIgnoreCase))
                {
                    var separatorIndex = trimmed.IndexOf("->", StringComparison.Ordinal);
                    if (separatorIndex > 0)
                    {
                        var key = CleanStructuredMemoryKey(trimmed[..separatorIndex]);
                        var value = CleanStructuredMemoryValue(trimmed[(separatorIndex + 2)..]);
                        if (IsValidMemoryCandidate(key, value))
                        {
                            candidates.Add(new MemoryCandidate(key, value, IsExplicitWrite: false, MemoryTypeForKey(key)));
                        }
                    }
                }
                else if (string.Equals(structuredBlock, "SCHEDULE CANDIDATES", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.TrimStart('-', '*', ' ').Split('|');
                    if (parts.Length >= 2)
                    {
                        var title = parts[0].Trim();
                        var when = parts[1].Trim();
                        var details = parts.Length >= 3 ? parts[2].Trim() : string.Empty;
                        // A timestamp in details means a second date leaked in (move artifact) - drop it.
                        if (IsoTimestampRegex.IsMatch(details))
                        {
                            details = string.Empty;
                        }

                        if (!string.IsNullOrWhiteSpace(title)
                            && !string.IsNullOrWhiteSpace(when)
                            && !string.Equals(title, "title", StringComparison.OrdinalIgnoreCase)
                            && !IsoTimestampRegex.IsMatch(title))
                        {
                            scheduleCandidates.Add(new ScheduleCandidate(title, when, details));
                        }
                    }
                }
                else if (string.Equals(structuredBlock, "TASK CANDIDATES", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.TrimStart('-', '*', ' ').Split('|');
                    var title = parts[0].Trim();
                    if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, "title", StringComparison.OrdinalIgnoreCase))
                    {
                        taskCandidates.Add(new TaskCandidate(title));
                    }
                }
                else if (string.Equals(structuredBlock, "TASK COMPLETE", StringComparison.OrdinalIgnoreCase))
                {
                    var title = trimmed.TrimStart('-', '*', ' ').Split('|')[0].Trim();
                    if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, "title", StringComparison.OrdinalIgnoreCase))
                    {
                        taskCompleteCandidates.Add(new TaskCompleteCandidate(title));
                    }
                }
                else if (string.Equals(structuredBlock, "CALENDAR DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.TrimStart('-', '*', ' ').Split('|');
                    var title = parts[0].Trim();
                    var when = parts.Length >= 2 ? parts[1].Trim() : null;
                    if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, "title", StringComparison.OrdinalIgnoreCase))
                    {
                        deleteCandidates.Add(new DeleteCandidate(title, string.IsNullOrWhiteSpace(when) ? null : when));
                    }
                }
                else if (string.Equals(structuredBlock, "CALENDAR MOVE", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.TrimStart('-', '*', ' ').Split('|');
                    var title = parts[0].Trim();
                    if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, "title", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                    {
                        var newWhen = parts[^1].Trim();
                        var when = parts.Length >= 3 ? parts[1].Trim() : null;
                        if (!string.IsNullOrWhiteSpace(newWhen))
                        {
                            moveCandidates.Add(new MoveCandidate(title, string.IsNullOrWhiteSpace(when) ? null : when, newWhen));
                        }
                    }
                }
                else if (string.Equals(structuredBlock, "CALENDAR EDIT", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.TrimStart('-', '*', ' ').Split('|');
                    var matchTitle = parts[0].Trim();
                    if (!string.IsNullOrWhiteSpace(matchTitle) && !string.Equals(matchTitle, "title", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                    {
                        static string? Field(string[] p, int i) =>
                            p.Length > i && !string.IsNullOrWhiteSpace(p[i]) ? p[i].Trim() : null;

                        var matchWhen = Field(parts, 1);
                        var newTitle = Field(parts, 2);
                        var newWhen = Field(parts, 3);
                        var newDetails = Field(parts, 4);
                        if (newTitle is not null || newWhen is not null || newDetails is not null)
                        {
                            editCandidates.Add(new EditCandidate(matchTitle, matchWhen, newTitle, newWhen, newDetails));
                        }
                    }
                }
                else if (string.Equals(structuredBlock, "DEV NOTE", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(structuredBlock, "DEV NOTES", StringComparison.OrdinalIgnoreCase))
                {
                    // Collected as task candidates with a special prefix; processed in ApplyStructuredMemoryAsync
                    var noteText = trimmed.TrimStart('-', '*', ' ');
                    if (!string.IsNullOrWhiteSpace(noteText))
                        devNoteCandidates.Add(noteText);
                }

                continue;
            }

            // Fallback: catch headerless pipe-separated schedule lines the LLM wrote as plain text.
            var fallbackTarget = trimmed.TrimStart('-', '*', ' ');
            var fallbackMatch = ScheduleLineFallbackRegex.Match(fallbackTarget);
            // Exactly one timestamp is a real add; two or more means a date leaked into the
            // title/details (a botched move) - ignore it rather than create a corrupt row.
            if (fallbackMatch.Success && string.IsNullOrEmpty(structuredBlock)
                && IsoTimestampRegex.Matches(fallbackTarget).Count == 1)
            {
                var title = fallbackMatch.Groups["title"].Value.Trim();
                var when = fallbackMatch.Groups["when"].Value.Trim();
                var details = fallbackMatch.Groups["details"].Value.Trim();
                if (!string.Equals(title, "title", StringComparison.OrdinalIgnoreCase)
                    && !IsoTimestampRegex.IsMatch(title))
                {
                    scheduleCandidates.Add(new ScheduleCandidate(title, when, details));
                    continue;
                }
            }

            visibleLines.Add(line);
        }

        return new ParsedAssistantResponse(string.Join(Environment.NewLine, visibleLines).Trim(), candidates, scheduleCandidates, taskCandidates, taskCompleteCandidates, deleteCandidates, moveCandidates, editCandidates, calendarDirective, devNoteCandidates);
    }

    private static bool TryParseCalendarDirective(string line, out CalendarDirective? directive)
    {
        directive = null;

        var candidate = line.Trim().Trim('`', '*', '_').Trim();

        var match = Regex.Match(
            candidate,
            @"^CALENDAR VIEW\s*:\s*(week|two[\s-]?week|fortnight|month)(?:\s*(?:starting|from|beginning)?\s*(\d{4}-\d{2}-\d{2}))?\.?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return false;
        }

        var modeText = match.Groups[1].Value.ToLowerInvariant().Replace(" ", string.Empty).Replace("-", string.Empty);
        var mode = modeText switch
        {
            "week" => "Week",
            "twoweek" or "fortnight" => "TwoWeek",
            _ => "Month"
        };

        DateTime? anchor = match.Groups[2].Success && DateTime.TryParse(match.Groups[2].Value, out var parsedDate)
            ? parsedDate.Date
            : null;

        directive = new CalendarDirective(mode, anchor);
        return true;
    }

    private static string RemoveLeakedTimestampLines(string content)
    {
        var lines = content.Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => !IsLeakedTimestampLine(line.Trim()));

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static bool IsLeakedTimestampLine(string line)
    {
        return line.StartsWith("[timestamp:", StringComparison.OrdinalIgnoreCase)
            && line.EndsWith(']');
    }

    private static bool IsStructuredBlockHeader(string line)
    {
        var normalized = NormalizeStructuredBlockHeader(line);
        return string.Equals(normalized, "MEMORY CANDIDATES", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "SCHEDULE CANDIDATES", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "TASK CANDIDATES", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "TASK COMPLETE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "CALENDAR DELETE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "CALENDAR MOVE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "CALENDAR EDIT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "TEMPORAL PATTERNS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "QUESTIONS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "DEV NOTE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "DEV NOTES", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStructuredBlockHeader(string line)
    {
        return line.Trim().TrimEnd(':').ToUpperInvariant();
    }

    private static string CleanStructuredMemoryKey(string key)
    {
        return Regex.Replace(key.Trim(), @"^[-*]\s*", string.Empty, RegexOptions.CultureInvariant).Trim();
    }

    private static string CleanStructuredMemoryValue(string value)
    {
        var cleaned = value.Trim();
        var metadataIndex = cleaned.IndexOf(" | ", StringComparison.Ordinal);
        if (metadataIndex >= 0)
        {
            cleaned = cleaned[..metadataIndex];
        }

        return CleanMemoryValue(cleaned);
    }

    private static bool IsValidMemoryCandidate(string key, string value)
    {
        return !string.IsNullOrWhiteSpace(key)
            && !string.IsNullOrWhiteSpace(value)
            && key.Length <= 100
            && !string.Equals(key, "key", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "value", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record MemoryCandidate(string Key, string Value, bool IsExplicitWrite, string Type);

    private sealed record MemoryValidationResult(string Status, string Error, bool ShouldWrite);

    private sealed record ScheduleCandidate(string Title, string When, string Details);

    private sealed record TaskCandidate(string Title);

    private sealed record TaskCompleteCandidate(string Title);

    private sealed record DeleteCandidate(string Title, string? When);

    private sealed record MoveCandidate(string Title, string? When, string NewWhen);

    private sealed record EditCandidate(string Title, string? When, string? NewTitle, string? NewWhen, string? NewDetails);

    private sealed record CalendarDirective(string Mode, DateTime? Anchor);

    private sealed record ParsedAssistantResponse(string VisibleContent, IReadOnlyList<MemoryCandidate> MemoryCandidates, IReadOnlyList<ScheduleCandidate> ScheduleCandidates, IReadOnlyList<TaskCandidate> TaskCandidates, IReadOnlyList<TaskCompleteCandidate> TaskCompleteCandidates, IReadOnlyList<DeleteCandidate> DeleteCandidates, IReadOnlyList<MoveCandidate> MoveCandidates, IReadOnlyList<EditCandidate> EditCandidates, CalendarDirective? CalendarDirective, IReadOnlyList<string> DevNoteCandidates);

    private sealed record ScopedChatSuggestion(string Title, string SeedPrompt);
}
