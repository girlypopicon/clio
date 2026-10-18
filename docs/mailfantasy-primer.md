# MailFantasy primer (for a fresh Claude chat)

## Who and what
I'm Stella. I'm building **MailFantasy**: my own email client plus the calendar, reminders and sticky notes for everything *outside* my personal assistant. Google only lets 10 Gmail accounts be signed in at once in a browser and I have 20+, so I need a custom app that handles as many accounts as I want.

It sits next to **CLIo**, my local-first personal assistant (Windows desktop app, Avalonia/.NET 9, local encrypted SQLite, DeepSeek for chat, voice in/out). I talk to CLIo all day; it quietly files facts, tasks and events into its own database. CLIo and MailFantasy are separate apps that must work without each other.

## Who owns what
- **MailFantasy:** all my email accounts; the external calendar (deadlines, appointments from outside); reminders and how they are delivered (desktop popups, content pushed to GlowRom's idle screen through GlowRom's API, later my phone); sticky notes.
- **CLIo:** conversation, memory, its own personal calendar/tasks, working out "remind me about X Sunday" from what I say.
- CLIo will talk to MailFantasy only through a small **read-only local API** (and, later, reminder creation). CLIo will not speak IMAP or CalDAV itself.

## Principles (please follow these)
1. **Manual edits are the source of truth.** Anything I change by hand must never be silently overwritten by an import, sync or automation. Conflicts get queued for me to decide.
2. Every record carries a **`source`** (who set it) and an **`updated_at`**. Imported rows are never marked as my manual edits.
3. **Honest actions:** never say something was sent, saved or deleted unless it was.
4. **Anything that changes the outside world (send mail, accept invites, delete) shows a dry-run preview and needs my approval.** Read-only first, sending last.
5. Local-first and private. No cloud storage of my mail or calendar unless I choose it.
6. Plain, direct answers. I often dictate by voice, so expect transcription slips; ask if something looks like a mishearing.

## Interface CLIo will use (first version)
`GET /events?from=&to=` (list), `POST /events`, `PATCH /events/{id}`, `DELETE /events/{id}`, plus `GET /changes?since=` so CLIo can sync without re-reading everything. Event fields: stable `id`, `title`, `start` and `end` in UTC plus a timezone name, `all_day`, `details`, `source`, `updated_at`. Reminders live next to events (due time, done flag) so popups, what gets pushed to GlowRom and my phone all read one list. Sticky notes are free text with no time.

## Decisions so far
- Calendar store is **separate** from CLIo's database, so it works when CLIo is closed.
- **Store UTC, render local**, and keep a timezone name per event so repeating events survive daylight saving.
- Phone access will be needed. A local-only server will not reach it, so plan a tunnel or small public endpoint with **real authentication** (token per device or Cloudflare Access). Do not copy the open, rate-limit-only pattern used for CLIo's speech Worker; this is private data.

## Suggested build order
1. Calendar store + the four endpoints + `changes` (my deadlines need a home first).
2. Reminders and desktop popups; sticky notes.
3. Email: IMAP **read-only** across the accounts (auth per account via app passwords or OAuth, scripted once), then classification (receipts, notifications, real people, junk), and sending **last**.
4. Pushing items to GlowRom through its API (GlowRom owns its own idle screen; MailFantasy only sends it things to display), then phone access.

## My setup
Windows 10, PowerShell, a Cloudflare account, my own servers. I work locally on `main` and do not use GitHub for this right now.

## Open questions for you to settle with me
Tech stack for the app, how to store 20+ accounts' credentials safely (Windows DPAPI is what CLIo uses), and how repeating events and reminders should be modeled.
