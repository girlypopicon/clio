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
- **ViewModels/** — `MainWindowViewModel` contains UI state, commands, and orchestration flow. It is a partial class: the main file holds state, commands and the chat flow; `.Voice`, `.Review` (review/approval/tidy/updates), `.Commands` (prefix and `__` commands), `.CalendarParsing`, `.MemoryCapture` and `.ResponseParsing` hold the rest.
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
- `__repeat #<id> <pattern>` — make a calendar event repeat (`every tuesday`, `weekdays`, `every 2 weeks`, `monthly until 2026-12-31`, or `off`)
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
- **Updates:** the version lives only in `<Version>` in `CLIo.csproj` (`AppVersion`). `UpdateService` reads the repo's latest GitHub release (about daily, or on request via `check_for_updates` / `__update`) and shows a banner. `install_update` (an `Acts` skill; also the banner's INSTALL button) installs it: `UpdateInstaller` verifies the ECDSA signature of `manifest.json` against the public key in `UpdateTrust.cs`, checks size/SHA-256, unpacks safely, backs up the database, and a PowerShell swap step (`UpdateApplyScript`) replaces the files after CLIo exits and rolls back if the new version dies. The private signing key never leaves the maintainer's PC (`tools/New-ReleaseKey.ps1`); release process in `docs/RELEASING.md`. Source/dev runs refuse to self-update. Bump `<Version>` and add a patch-notes entry in `SystemPromptBuilder` for each release.
- **Acts skills:** `ISkill.PreviewAsync` is the dry run; `SkillRegistry` asks `IApprovalProvider` (the main view model's approval modal) before `RunAsync`, and without an approval UI an Acts skill cannot run.
- **Runtime log:** `RuntimeLog` records structured events (time, component, type, outcome, latency, model, tokens, short detail) into the encrypted `runtime_events` table, newest 3,000 kept across restarts. Recorded explicitly at: every LLM call (`ResilientLlmProvider`, purpose from `LlmCall.Use("extract"|"polish"|...)`, real token counts from DeepSeek's usage chunk), skill runs, the extraction pass, memory writes, calendar and task changes, review decisions, update checks/installs, app start/exit and unhandled exceptions. CLIo reads it with the `query_runtime_log` skill and must not claim background activity without it; the user reads it with `__events since=1h component=llm`. When you add a component that does something real in the background, `Record()` it.
- **Source-copy updates:** a copy run from a git checkout (`dotnet run` in the repo) updates through `SourceUpdater`: `check_for_updates` and the banner compare with `origin/main`; `update_from_source` (an `Acts` skill) does `git pull --ff-only` (only main, only this repo's GitHub origin, never over modified tracked files), builds into a temp folder to prove it compiles (undoes the pull if not), and tells the user to restart. CLIo never restarts herself.
- **read_source:** reads one source file (line numbers, optional range) or lists a folder, confined to the repo root; refuses build output, `.git`, secrets, non-text files and links that leave the folder.
- **Tidy and corrections:** `TidyScanner` (pure, rule-based) finds duplicate memory keys, value-and-parent keys, stray-colon twins, duplicate events and tasks; findings become Review items (`merge_memory`, `rename_memory`, `duplicate_event`, `duplicate_task`) that `TidyApplier` carries out only when the user accepts. Runs about nightly and with `__tidy`. Every manual edit and Review decision is also written to the permanent `corrections` table (`__corrections`).
- **Prompt layout rule:** `SystemPromptBuilder` puts everything stable first (so DeepSeek's prefix cache hits) and everything that changes per message (counters, memory, calendar, tasks, time) last. Keep it that way.
- **Self-check:** `CLIo.exe --validate-services` verifies the DI wiring without creating anything or touching data (exit code 0 = fine, text in the file named by `CLIO_VALIDATE_OUT`). Run it after changing constructors or `Startup.cs`.
- **Planned (not built):** texting (an `Acts` skill, always approved), a GlowRom connector, and the user's own email app feeding outside-calendar events in as inbound data with its own `source` value (never `manual`, so manual edits still win).

## Recurring events

`calendar_events.recurrence` holds an RFC 5545 RRULE subset (`FREQ`, `INTERVAL`, `BYDAY` for weekly, `UNTIL`, `COUNT`) parsed and expanded by `RecurrenceRule`. The stored row is the series; its own start is the first occurrence. `CalendarService.GetAllEventsAsync` returns computed occurrences (30 days back to 120 ahead) that share the series' `Id` and have `Recurrence` set (`IsOccurrence`). Edits, moves and deletes by `Id` apply to the whole series (moves keep the series' first date and take only the time of day); per-occurrence exceptions and a `temporal_log` audit table are not built. Reminders are created once per occurrence. The tidy scan ignores occurrences.

## Database Schema

Tables are created/migrated in `JournalDatabaseService.Initialize()`. Key tables: `chat_threads`, `chat_messages`, `memory_items`, `memory_transactions`, `calendar_events`, `calendar_candidates`, `journal_tasks`, `journal_entries`, `settings`. Database is encrypted with SQLCipher; master key is DPAPI-protected in `%LOCALAPPDATA%/CLIo/master.key`.

## UI Views

The app has four top-level views switched by the top bar: **Chat**, **Calendar**, **Memory**, **Tasks**. Settings opens as a modal overlay. The chat view has a left sidebar (thread list) and right tools panel. Memory view has 10 category buttons + Dates. Dark theme only — colors defined in `spec1.txt`.

## DeepSeek API Key

Stored encrypted (DPAPI) in the `settings` SQLite table and loaded into `DEEPSEEK_API_KEY` env var at startup. Model defaults to `deepseek-chat`, overridable via `DEEPSEEK_MODEL` env var.
