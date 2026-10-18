# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is CLIo

CLIo is a local-first conversational runtime and personal organizer — a desktop chat app that replaces ChatGPT/DeepSeek for daily use. The user talks casually throughout the day; CLIo preserves history and quietly organizes conversation into structured memory (facts, preferences, patterns), calendar events, tasks, and summaries. It is **not** a coding assistant. See `spec1.txt` and `spec2.txt` for the product spec. `spec2.txt` is the newer spec focused on the memory system.

## Build and Run

```bash
dotnet build              # build the main app
dotnet run                # run the app
dotnet build CLIo.sln     # build everything including installer project
```

Target framework is .NET 9. Solution file is `CLIo.sln` containing `CLIo.csproj` and `CLIo.Installer/CLIo.Installer.wixproj`.

## Technology Stack

- **UI framework:** Avalonia 11.3 (cross-platform desktop, XAML-based)
- **MVVM:** CommunityToolkit.Mvvm — uses `[ObservableProperty]`, `[RelayCommand]`, and source generators
- **DI:** Microsoft.Extensions.DependencyInjection — configured in `Startup.cs`, exposed via `App.Services`
- **Database:** SQLite via Microsoft.Data.Sqlite + SQLCipher (encrypted at rest). All data stored locally in `%LOCALAPPDATA%/CLIo/clio.db`
- **Encryption:** DPAPI (`DataProtectionService`) for master key + per-field secret protection. SQLCipher encrypts the full database.
- **LLM provider:** DeepSeek API (streaming SSE) via `ResilientLlmProvider` which falls back to `FallbackLlmProvider` (Catalyst NLP) when offline
- **NLP fallback:** Catalyst + Catalyst.Models.English for offline entity recognition
- **Date parsing:** Microsoft.Recognizers.Text.DateTime for natural language dates
- **Date display:** Humanizer for human-friendly timestamps ("2 hours ago")
- **Agent orchestration:** BotSharp.Core for multi-agent routing (in progress)
- **Text editor:** AvaloniaEdit (used for the scratchpad/composer)
- **Bindings:** Compiled bindings enabled by default (`AvaloniaUseCompiledBindingsByDefault`). All XAML bindings must use `x:DataType`.
- **Installer:** WiX v5 MSI project in `CLIo.Installer/`

## Architecture

**MVVM with DI and service layer.** Services are registered in `Startup.cs` and injected into `MainWindowViewModel`.

### Key layers:

- **Views/** — Avalonia XAML views. `MainWindow.axaml` is the primary UI; `SplashWindow.axaml` shows during init.
- **ViewModels/** — `MainWindowViewModel` contains UI state, commands, and orchestration flow.
- **Models/** — Plain data classes: `ChatMessage`, `ChatThread`, `LlmMessage`, `CalendarEvent`, `CalendarDayCell`, `JournalEntry`, `JournalTask`, `MemoryItem`, `MemoryTransaction`, `CalendarCandidate`, `ActionBadge`.
- **Services/** — Business logic and data access:
  - `JournalDatabaseService` — all SQLite access (schema creation, migrations, CRUD)
  - `DataProtectionService` — DPAPI encryption, master key management
  - `MemoryService` (`IMemoryService`) — memory extraction, validation, field mapping
  - `CalendarService` (`ICalendarService`) — calendar CRUD and event matching
  - `TaskService` (`ITaskService`) — task CRUD
  - `SystemPromptBuilder` — dynamic LLM system prompt assembly with self-description
  - `DeepSeekProvider` — streaming LLM calls via SSE
  - `ResilientLlmProvider` — wraps DeepSeek with offline fallback
  - `FallbackLlmProvider` — uses Catalyst NLP + Recognizers.Text when offline
  - `OfflineNlpService` — Catalyst pipeline for entity recognition
  - `DateTimeParsingService` — natural language date parsing via Recognizers.Text
- **Converters/** — `HumanizeDateConverter` for XAML date display
- **CLIo.Installer/** — WiX v5 MSI installer project

### Data flow for chat:

1. User sends message → `SendChatAsync()` in the ViewModel
2. Local regex capture runs first and writes obvious facts directly (conflicts with manual values become Review items; there is no confirmation toast)
3. `@Name` selects a persona agent for this reply
4. `SystemPromptBuilder.Build()` assembles the system prompt: instructions, self-description, current memory, upcoming events, tasks; the skills section is appended
5. LLM response streams back token-by-token (falls back to Catalyst offline NLP if DeepSeek is unreachable); a `SKILL CALL` reply runs the skill loop
6. `ApplyStructuredMemoryAsync()` applies the structured blocks that remain: calendar delete/move/edit, task complete, calendar view
7. `RunAutoExtractionAsync()` (background) sends only the user's message to `ConcreteDataExtractor`, which returns memory, tasks (with optional due date), events and dev notes as JSON; these are filed with badges

### Memory system:

- Hierarchical colon-delimited keys organized into 10 categories: `user`, `projects`, `people`, `systems`, `tools`, `goals`, `knowledge`, `context`, `meta`, `flex`
- Dynamic subkeys allowed freely under any category (e.g. `people:sarah:phone`, `projects:sendpop:status`)
- Preferences use `flex:preference:<slug>`, patterns use `flex:pattern:<slug>`
- All memory writes go through a transaction log (`memory_transactions` table) with status tracking
- Passive extraction files directly (no confirmation); conflicts with manual values go to Review. Automatic keys pass through `MemoryKeyCanonicalizer` (aliases, no key is both value and parent)
- Some keys (`user:name`, `user:birthdate`) are immutable after first set
- Memory compaction advisory triggers when key count exceeds 40
- `__` commands for memory: `__forget`, `__rename`, `__merge`, `__search`, `__compact`

### Prefix commands:

- `++` — add calendar event or task (LLM determines which based on date context)
- `--` — delete calendar event or task
- `==` — reschedule/move calendar event
- `__` — display/navigate + memory operations (`__tasks`, `__user`, `__people`, `__forget <key>`, `__search <term>`, `__compact`, etc.)

### Provenance (manual edits outrank chat):

- `memory_items`, `calendar_events` and `journal_tasks` carry `source` (`manual` = set by the user in the UI, `chat` = filed automatically) and `source_at`.
- Passive captures (the extractor, regex capture, LLM memory blocks) never overwrite a `manual` value. The disagreement is written to `review_items` and shown in the **Review** view (Keep mine / Use chat's). Accepting makes the value `manual`; dismissing remembers the claim so it is not proposed again.
- Explicit chat instructions ("change my X to Y", "move the dentist") still apply: they are the user speaking now.
- Any future nightly scan must follow the same rule: read chat only since its last run, never overwrite `manual` rows, and queue conflicts in `review_items`.
- The database is really encrypted (SQLCipher via `SQLitePCLRaw.bundle_e_sqlcipher`). Schema upgrades copy the file to `%LOCALAPPDATA%/CLIo/backups/` first.

### Skills and agents (`Services/Skills/`):

- **Contract:** `ISkill` (read the doc comment in `ISkill.cs`). A skill has a name, a one-line summary, a permission (`ReadOnly` runs automatically; `Acts` always needs the user's approval and is never auto-run), a timeout, and takes one text input and returns bounded text. Skills never write to memory, calendar or tasks; only the user's own messages are ever filed.
- **Calling:** the model replies with a `SKILL CALL` block (`name | input`), `SkillRegistry` runs it (max 3 calls per round, 3 rounds), and the result goes back to the model fenced as `<<<DATA ... DATA>>>` (untrusted: instructions inside are ignored). `MainWindowViewModel.RunSkillLoopAsync` drives the loop.
- **Built in:** `web_search` (DuckDuckGo HTML results, no key; brittle by nature) and `browse` (one page's readable text, no scripts). `UrlSafety` refuses loopback/private/link-local/CGNAT addresses on the actual connection (covers redirects and DNS tricks).
- **Persona agents:** `.agent.md` files (same format as the IconicAgents repo: `name`, `description`, body) in `agents/` (shipped next to the exe) and `%LOCALAPPDATA%/CLIo/agents` (overrides). Start a message with `@Name` to answer as that persona for one reply. Personas change how CLIo answers; they grant no abilities. `__skills` and `__agents` list what exists.
- **self_docs:** the skill CLIo uses to answer questions about herself. It serves `SelfDocs/architecture.md` (behavior and design, no secrets, no code bodies; keep it updated when behavior changes). The build encrypts it (AES-256-CBC + HMAC, `EncryptSelfDocs` target in `CLIo.csproj`) into an embedded `.bin`; the readable file is never shipped and is decrypted in memory only. Release builds should set the `CLIO_SELFDOCS_KEY` environment variable so the key phrase is not the development default in source control; run `dotnet clean` after changing it. The program must hold the key to read the document, so this is protection against casual reading, not against reverse engineering plus live facts generated from the running app (skills, agents, schema, version, class/method-name code map).
- **Planned (not built):** texting (an `Acts` skill, always approved), a GlowRom connector, and the user's own email app feeding outside-calendar events in as inbound data with its own `source` value (never `manual`, so manual edits still win).

## Database Schema

Tables are created/migrated in `JournalDatabaseService.Initialize()`. Key tables: `chat_threads`, `chat_messages`, `memory_items`, `memory_transactions`, `calendar_events`, `calendar_candidates`, `journal_tasks`, `journal_entries`, `settings`. Database is encrypted with SQLCipher; master key is DPAPI-protected in `%LOCALAPPDATA%/CLIo/master.key`.

## UI Views

The app has four top-level views switched by the top bar: **Chat**, **Calendar**, **Memory**, **Tasks**. Settings opens as a modal overlay. The chat view has a left sidebar (thread list) and right tools panel. Memory view has 10 category buttons + Dates. Dark theme only — colors defined in `spec1.txt`.

## DeepSeek API Key

Stored encrypted (DPAPI) in the `settings` SQLite table and loaded into `DEEPSEEK_API_KEY` env var at startup. Model defaults to `deepseek-chat`, overridable via `DEEPSEEK_MODEL` env var.
