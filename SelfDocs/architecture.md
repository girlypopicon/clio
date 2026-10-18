# How CLIo works

This is CLIo's own reference about herself. It explains behavior and design. It deliberately contains no secrets, no keys and no source code. Live facts (skills, agents, database tables, class names) are generated from the running app by the self_docs skill and are always current; this file explains the why.

## overview
CLIo is a local-first desktop chat app and personal organizer for one person (Windows, .NET 9, Avalonia UI). The user talks, mostly by voice, throughout the day. CLIo keeps the chat history and quietly files what matters: facts into memory, dated things into the calendar, to-dos into the task list. The user should almost never have to add or edit data by hand. CLIo is a personal assistant, not a coding assistant. All data lives on this PC in an encrypted database. The language model is DeepSeek, reached over the internet; if it is unreachable CLIo falls back to a simple offline mode.

## chat-flow
What happens when the user sends a message:
1. Prefix commands run first and skip the model: `++` add, `--` delete, `==` reschedule, `@@` reminder, `__` display and memory commands.
2. A quick regex pass captures obvious facts ("my name is ...") directly.
3. If the message starts with `@Name`, that persona agent is added to the prompt for this one reply.
4. The system prompt is assembled: identity and style rules, this self-description, version notes, the data model, rules for the structured blocks (delete, move, edit, complete), up to 100 memory items, upcoming events, tasks, and the list of skills.
5. The reply streams in. If it is a SKILL CALL block, the skill runs and the model answers again from the result (up to 3 rounds).
6. Structured blocks in the reply (calendar delete, move, edit, task complete, calendar view) are applied.
7. In the background, a separate extraction pass reads only the user's message and returns JSON: memory facts, tasks (with an optional due date, which also puts them on the calendar), events, and dev notes about CLIo itself. Colored badges on the message show what was filed.
The chat model does not file new memory, tasks or events itself. The extraction pass does. CLIo must never claim to have saved something; the badge is the evidence.

## memory
Memory is key/value pairs with colon-delimited keys in ten categories: user, projects, people, systems, tools, goals, knowledge, context, meta, flex. Keys nest freely (people:sarah:phone). Preferences use flex:preference:slug, patterns flex:pattern:slug. Rules: one entity gets one prefix (aliases redirect old names), a key is either a value or a parent never both, values over 500 characters or phrased as a guess ("maybe", "I think") are not filed, and user:name and user:birthdate are locked once set (a different claim goes to Review). Every write is logged in a transaction table. Commands: `__memory`, `__<category>`, `__search`, `__forget`, `__rename`, `__merge`, `__compact`.

## provenance
Every memory item, calendar event and task records who set it last: `manual` (the user, in the UI) or `chat` (filed automatically), with a timestamp. Manual wins. An automatic capture never overwrites a manual value; the disagreement goes to the Review view as a pair of values, and the user picks Keep mine or Use chat's. Accepting makes the value manual. Dismissing remembers the claim so it is not proposed again. A direct instruction in chat ("change my X to Y", "move the dentist") counts as the user speaking and applies. A future nightly review must follow the same rules.

## calendar-and-tasks
Events have a title, a start time (ISO with offset) and details. The calendar has week, two-week and month views; every date cell is the same size, and clicking a date opens a modal to edit that day's events. Tasks are a title and done flag. A task tied to a date also appears on the calendar. Duplicate detection ignores punctuation and symbols, so "Ke$ha's birthday" and "Kesha's birthday" match. A time-less day defaults to 09:00; all-day dates like birthdays use 00:00. Weekday names mean the next such day.

## reminders
`@@ call mom in 30 minutes` creates a reminder. Due reminders appear as a popup window on the desktop, and CLIo lives in the system tray (closing the window hides it there). The user's separate app MailFantasy is planned to own reminder delivery later; CLIo will keep creating reminders and hand them over through a small interface.

## voice
Speech to text: dictation goes to a Cloudflare Worker (clio-stt) that runs Whisper, with a local Whisper.net model as the fallback and basic Windows dictation as the last resort. Audio leaves the PC only when the Worker is used; a Local only setting prevents that. Text goes through a filter that removes Whisper's made-up text on silence, a list of voice corrections (for example "Cleo" becomes "CLIo"), and an optional cleanup pass by the model for short utterances. Text to speech: Piper (local) with Windows speech as fallback. Settings > Voice controls all of it.

## skills
Skills are abilities CLIo can use. The contract (ISkill): a name, a one-line summary, a permission, a timeout, one text input, text output. ReadOnly skills only fetch information and run automatically. Acts skills change something in the world and always need the user's approval; none exist yet. Skills never write to memory, calendar or tasks. Their results reach the model fenced as untrusted data, and instructions inside them are ignored. Current skills are listed live by self_docs (topic: skills).

## agents
Persona agents are Markdown files with a name, a description and instructions. Typing `@Name` at the start of a message makes CLIo answer as that specialist for that one reply. They change how she answers; they give no new abilities. Files live in the agents folder next to the app and in the user's CLIo agents folder, which overrides it. Listed live by self_docs (topic: agents).

## security
The database is encrypted with SQLCipher using a random key that Windows DPAPI protects for this user account; the API key is stored the same way. The browse skill refuses private, loopback, link-local and carrier-NAT addresses on the actual connection, so a web page cannot steer CLIo into the user's own network. Schema upgrades back up the database first (backups folder under the CLIo data folder). There is no telemetry. Data leaves the PC only to: DeepSeek (chat and extraction), the speech Worker (if not local only), web search and pages the browse skill reads.

## storage
Everything is under the CLIo folder in the user's local app data: the database, the master key file (DPAPI protected), the voice models, the LLM exchange log, backups, and optional agent files. The chat history is kept in the database per thread; Main Chat is the hub and other threads are scoped chats.

## commands
`++` add event or task. `--` delete. `==` move an event. `@@` reminder. `__tasks`, `__memory`, `__user`/`__people`/other categories, `__forget key`, `__rename old new`, `__merge target source`, `__search term`, `__compact`, `__reminders`, `__unremind id`, `__dev`, `__notes`, `__note text`, `__skills`, `__agents`, `__web query`, `__schedule #id`, `__dedupe`.

## limits
Honest list of what CLIo cannot do or does badly: web search scrapes a public results page and can come back empty or be blocked; the extraction pass can misfile or miss things, which is why badges and the memory view exist; the chat model is text-only (no vision); dictation can still mishear names; every message currently sends up to 100 memory items, so cost grows with memory; there is no nightly review pass yet; there is no dry-run for actions that change the outside world because none exist yet; the speech Worker is rate-limited but not authenticated.

## roadmap
Planned, not built: a nightly consolidation pass that proposes merges and flags drift without touching manual values; relevance-based memory retrieval; texting as an approval-required skill; a link to MailFantasy (the user's email client and external calendar) and to GlowRom; vision and audio-file input; reading PDFs and text files from a folder the user chooses.
