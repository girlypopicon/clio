using CLIo.Models;
using Microsoft.Data.Sqlite;

namespace CLIo.Services;

public sealed class JournalDatabaseService
{
    private readonly string connectionString;
    private readonly string dbFilePath;
    private readonly DataProtectionService dataProtection;

    public JournalDatabaseService(DataProtectionService dataProtection)
    {
        this.dataProtection = dataProtection;
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLIo");
        Directory.CreateDirectory(appData);
        var dbPath = Path.Combine(appData, "clio.db");
        dbFilePath = dbPath;
        connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        SQLitePCL.Batteries_V2.Init();
        MigrateToEncryptedDatabase(dbPath);
        Initialize();
        MigrateToHierarchicalKeys();
    }

    /// <summary>
    /// Encrypts an existing plain SQLite database with SQLCipher. SQLCipher cannot rekey a plain
    /// file in place, so the data is exported into a new encrypted file, verified table by table,
    /// and only then swapped in. The original is kept as clio.db.plain-backup (delete it once you
    /// have confirmed CLIo works). On any failure the original file is left exactly as it was.
    /// </summary>
    private void MigrateToEncryptedDatabase(string dbPath)
    {
        if (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0)
            return;

        // A plain database opens without a key; an encrypted one throws "file is not a database".
        List<string> tables;
        try
        {
            tables = ListTables(dbPath, null);
        }
        catch
        {
            return; // already encrypted (or unreadable): never touch it
        }

        var tempPath = dbPath + ".enc.tmp";
        var backupPath = dbPath + ".plain-backup";
        var hex = dataProtection.MasterKeyHex;

        try
        {
            File.Delete(tempPath);

            var plainConnection = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
            using (var conn = new SqliteConnection(plainConnection))
            {
                conn.Open();
                using var export = conn.CreateCommand();
                export.CommandText =
                    $"ATTACH DATABASE '{tempPath.Replace("'", "''")}' AS encrypted KEY \"x'{hex}'\"; " +
                    "SELECT sqlcipher_export('encrypted'); DETACH DATABASE encrypted;";
                export.ExecuteNonQuery();
            }

            foreach (var table in tables)
            {
                var before = CountRows(dbPath, null, table);
                var after = CountRows(tempPath, hex, table);
                if (before != after)
                    throw new InvalidOperationException($"Row count mismatch in {table}: {before} vs {after}");
            }

            SqliteConnection.ClearAllPools();
            File.Move(dbPath, backupPath, overwrite: true);
            File.Move(tempPath, dbPath);
        }
        catch
        {
            // Leave the original database untouched; the app keeps working unencrypted.
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static List<string> ListTables(string path, string? hexKey)
    {
        var tables = new List<string>();
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        if (hexKey is not null)
        {
            using var key = conn.CreateCommand();
            key.CommandText = $"PRAGMA key = \"x'{hexKey}'\"";
            key.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static long CountRows(string path, string? hexKey, string table)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        if (hexKey is not null)
        {
            using var key = conn.CreateCommand();
            key.CommandText = $"PRAGMA key = \"x'{hexKey}'\"";
            key.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM \"{table.Replace("\"", "\"\"")}\"";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public Task<IReadOnlyList<JournalEntry>> GetEntriesAsync()
    {
        return Task.Run<IReadOnlyList<JournalEntry>>(() =>
        {
            var entries = new List<JournalEntry>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, entry_date, title, mood, body, updated_at FROM journal_entries ORDER BY entry_date DESC, id DESC";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new JournalEntry
                {
                    Id = reader.GetInt32(0),
                    EntryDate = reader.GetString(1),
                    Title = reader.GetString(2),
                    Mood = reader.GetString(3),
                    Body = reader.GetString(4),
                    UpdatedAt = reader.GetString(5)
                });
            }

            return entries;
        });
    }

    public Task SaveEntryAsync(int? id, string entryDate, string title, string mood, string body)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            if (id is null or 0)
            {
                command.CommandText = """
                    INSERT INTO journal_entries(entry_date, title, mood, body, created_at, updated_at)
                    VALUES ($entryDate, $title, $mood, $body, $now, $now)
                    """;
            }
            else
            {
                command.CommandText = """
                    UPDATE journal_entries
                    SET entry_date = $entryDate, title = $title, mood = $mood, body = $body, updated_at = $now
                    WHERE id = $id
                    """;
                command.Parameters.AddWithValue("$id", id.Value);
            }

            command.Parameters.AddWithValue("$entryDate", entryDate);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$mood", mood);
            command.Parameters.AddWithValue("$body", body);
            command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("g"));
            command.ExecuteNonQuery();
        });
    }

    public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync()
    {
        return Task.Run<IReadOnlyList<CalendarEvent>>(() =>
        {
            var events = new List<CalendarEvent>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, title, starts_at, details, source FROM calendar_events ORDER BY starts_at LIMIT 500";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!DateTimeOffset.TryParse(reader.GetString(2), out var startsAt))
                {
                    continue;
                }

                events.Add(new CalendarEvent
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    StartsAt = startsAt,
                    Details = reader.GetString(3),
                    Source = reader.GetString(4)
                });
            }

            return events;
        });
    }

    public Task AddEventAsync(string title, DateTimeOffset startsAt, string details, string source = "chat")
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO calendar_events(title, starts_at, details, source, source_at) VALUES ($title, $startsAt, $details, $source, $sourceAt)";
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$startsAt", startsAt.ToString("O"));
            command.Parameters.AddWithValue("$details", details);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$sourceAt", DateTimeOffset.Now.ToString("O"));
            command.ExecuteNonQuery();
        });
    }

    public Task<int> AddReminderAsync(string title, DateTimeOffset dueAt, int? sourceEventId = null)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO reminders(title, due_at, source_event_id) VALUES ($title, $dueAt, $eventId); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$dueAt", dueAt.ToString("O"));
            command.Parameters.AddWithValue("$eventId", (object?)sourceEventId ?? DBNull.Value);
            return Convert.ToInt32(command.ExecuteScalar());
        });
    }

    public Task<IReadOnlyList<Reminder>> GetPendingRemindersAsync()
    {
        return Task.Run<IReadOnlyList<Reminder>>(() =>
        {
            var reminders = new List<Reminder>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, title, due_at, source_event_id FROM reminders WHERE status = 'pending' ORDER BY due_at LIMIT 200";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!DateTimeOffset.TryParse(reader.GetString(2), out var dueAt))
                {
                    continue;
                }

                reminders.Add(new Reminder
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    DueAt = dueAt,
                    SourceEventId = reader.IsDBNull(3) ? null : reader.GetInt32(3)
                });
            }

            return reminders;
        });
    }

    public Task<bool> ReminderExistsForEventAsync(int eventId)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM reminders WHERE source_event_id = $eventId LIMIT 1";
            command.Parameters.AddWithValue("$eventId", eventId);
            return command.ExecuteScalar() is not null;
        });
    }

    public Task SetReminderStatusAsync(int id, string status)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE reminders SET status = $status WHERE id = $id";
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    public Task SnoozeReminderAsync(int id, DateTimeOffset dueAt)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE reminders SET due_at = $dueAt, status = 'pending' WHERE id = $id";
            command.Parameters.AddWithValue("$dueAt", dueAt.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    public Task DeleteEventAsync(int id)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM calendar_events WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    public Task UpdateEventStartAsync(int id, DateTimeOffset startsAt)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE calendar_events SET starts_at = $startsAt WHERE id = $id";
            command.Parameters.AddWithValue("$startsAt", startsAt.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    public Task UpdateEventAsync(int id, string title, DateTimeOffset startsAt, string details, string source = "chat")
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE calendar_events SET title = $title, starts_at = $startsAt, details = $details, source = $source, source_at = $sourceAt WHERE id = $id";
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$startsAt", startsAt.ToString("O"));
            command.Parameters.AddWithValue("$details", details);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$sourceAt", DateTimeOffset.Now.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    public Task<IReadOnlyList<JournalTask>> GetTasksAsync()
    {
        return Task.Run<IReadOnlyList<JournalTask>>(() =>
        {
            var tasks = new List<JournalTask>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, title, is_done FROM journal_tasks ORDER BY is_done, id DESC";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tasks.Add(new JournalTask
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    IsDone = reader.GetInt32(2) == 1
                });
            }

            return tasks;
        });
    }

    public Task AddTaskAsync(string title, string source = "chat")
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO journal_tasks(title, is_done, source, source_at) VALUES ($title, 0, $source, $sourceAt)";
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$sourceAt", DateTimeOffset.Now.ToString("O"));
            command.ExecuteNonQuery();
        });
    }

    public Task SetTaskDoneAsync(int id, bool isDone)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE journal_tasks SET is_done = $isDone WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$isDone", isDone ? 1 : 0);
            command.ExecuteNonQuery();
        });
    }

    public Task<bool> CompleteTaskByTitleAsync(string title)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE journal_tasks SET is_done = 1 WHERE LOWER(title) = LOWER($title) AND is_done = 0";
            command.Parameters.AddWithValue("$title", title.Trim());
            return command.ExecuteNonQuery() > 0;
        });
    }

    public Task DeleteTaskAsync(int id)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM journal_tasks WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    public Task<string> GetSettingAsync(string key, string fallback = "")
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string ?? fallback;
        });
    }

    public Task SetSettingAsync(string key, string value)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO settings(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        });
    }

    public Task<IReadOnlyList<ChatMessage>> GetChatMessagesAsync(int limit = 80)
    {
        return GetChatMessagesAsync(1, limit);
    }

    public Task<IReadOnlyList<ChatThread>> GetChatThreadsAsync()
    {
        return Task.Run<IReadOnlyList<ChatThread>>(() =>
        {
            var threads = new List<ChatThread>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, title, summary, is_main, updated_at FROM chat_threads ORDER BY is_main DESC, updated_at DESC";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                threads.Add(new ChatThread
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    Summary = reader.GetString(2),
                    IsMain = reader.GetInt32(3) == 1,
                    UpdatedAt = reader.GetString(4)
                });
            }

            return threads;
        });
    }

    public Task<ChatThread> AddChatThreadAsync(string title)
    {
        return Task.Run(() =>
        {
            var updatedAt = DateTimeOffset.Now.ToString("O");
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO chat_threads(title, is_main, updated_at) VALUES ($title, 0, $updatedAt)";
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$updatedAt", updatedAt);
            command.ExecuteNonQuery();

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid()";
            var id = Convert.ToInt32(idCommand.ExecuteScalar());

            return new ChatThread { Id = id, Title = title, Summary = string.Empty, IsMain = false, UpdatedAt = updatedAt };
        });
    }

    public Task UpdateChatThreadSummaryAsync(int threadId, string summary)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE chat_threads SET summary = $summary WHERE id = $threadId";
            command.Parameters.AddWithValue("$summary", summary);
            command.Parameters.AddWithValue("$threadId", threadId);
            command.ExecuteNonQuery();
        });
    }

    public Task UpdateChatThreadTitleAsync(int threadId, string title)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE chat_threads SET title = $title, updated_at = $updatedAt WHERE id = $threadId";
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
            command.Parameters.AddWithValue("$threadId", threadId);
            command.ExecuteNonQuery();
        });
    }

    public Task DeleteChatThreadAsync(int threadId)
    {
        return Task.Run(() =>
        {
            if (threadId == 1)
            {
                return;
            }

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using var messagesCommand = connection.CreateCommand();
            messagesCommand.Transaction = transaction;
            messagesCommand.CommandText = "DELETE FROM chat_messages WHERE thread_id = $threadId";
            messagesCommand.Parameters.AddWithValue("$threadId", threadId);
            messagesCommand.ExecuteNonQuery();

            using var threadCommand = connection.CreateCommand();
            threadCommand.Transaction = transaction;
            threadCommand.CommandText = "DELETE FROM chat_threads WHERE id = $threadId AND is_main = 0";
            threadCommand.Parameters.AddWithValue("$threadId", threadId);
            threadCommand.ExecuteNonQuery();

            transaction.Commit();
        });
    }

    public Task<IReadOnlyList<ChatMessage>> GetChatMessagesAsync(int threadId, int limit = 80)
    {
        return Task.Run<IReadOnlyList<ChatMessage>>(() =>
        {
            var messages = new List<ChatMessage>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = limit > 0
                ? """
                SELECT id, role, content, timestamp
                FROM chat_messages
                WHERE thread_id = $threadId
                ORDER BY id DESC
                LIMIT $limit
                """
                : """
                SELECT id, role, content, timestamp
                FROM chat_messages
                WHERE thread_id = $threadId
                ORDER BY id
                """;
            command.Parameters.AddWithValue("$threadId", threadId);
            if (limit > 0)
            {
                command.Parameters.AddWithValue("$limit", limit);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                messages.Add(new ChatMessage
                {
                    Id = reader.GetInt32(0),
                    Role = reader.GetString(1),
                    Content = reader.GetString(2),
                    Timestamp = DateTimeOffset.Parse(reader.GetString(3))
                });
            }

            if (limit > 0)
            {
                messages.Reverse();
            }

            return messages;
        });
    }

    public Task AddChatMessageAsync(string role, string content, DateTimeOffset timestamp)
    {
        return AddChatMessageAsync(1, role, content, timestamp);
    }

    public Task AddChatMessageAsync(int threadId, string role, string content, DateTimeOffset timestamp)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO chat_messages(thread_id, role, content, timestamp) VALUES ($threadId, $role, $content, $timestamp)";
            command.Parameters.AddWithValue("$threadId", threadId);
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$timestamp", timestamp.ToString("O"));
            command.ExecuteNonQuery();

            using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = "UPDATE chat_threads SET updated_at = $updatedAt WHERE id = $threadId";
            updateCommand.Parameters.AddWithValue("$updatedAt", timestamp.ToString("O"));
            updateCommand.Parameters.AddWithValue("$threadId", threadId);
            updateCommand.ExecuteNonQuery();
        });
    }

    public Task ClearChatMessagesAsync()
    {
        return ClearChatMessagesAsync(1);
    }

    public Task ClearChatMessagesAsync(int threadId)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM chat_messages WHERE thread_id = $threadId";
            command.Parameters.AddWithValue("$threadId", threadId);
            command.ExecuteNonQuery();
        });
    }

    public Task<int> TrimOversizedThreadsAsync(int maxMessagesPerThread)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();

            var threadIds = new List<int>();
            using var threadsCommand = connection.CreateCommand();
            threadsCommand.CommandText = "SELECT id FROM chat_threads";
            using var threadsReader = threadsCommand.ExecuteReader();
            while (threadsReader.Read())
            {
                threadIds.Add(threadsReader.GetInt32(0));
            }

            var trimmedThreads = 0;
            foreach (var threadId in threadIds)
            {
                using var countCommand = connection.CreateCommand();
                countCommand.CommandText = "SELECT COUNT(*) FROM chat_messages WHERE thread_id = $threadId";
                countCommand.Parameters.AddWithValue("$threadId", threadId);
                var count = Convert.ToInt32(countCommand.ExecuteScalar());

                if (count <= maxMessagesPerThread)
                {
                    continue;
                }

                var keepCount = (int)Math.Ceiling(count * 0.3);
                var deleteCount = count - keepCount;

                using var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = @"
                    DELETE FROM chat_messages
                    WHERE id IN (
                        SELECT id FROM chat_messages
                        WHERE thread_id = $threadId
                        ORDER BY id ASC
                        LIMIT $deleteCount
                    )";
                deleteCommand.Parameters.AddWithValue("$threadId", threadId);
                deleteCommand.Parameters.AddWithValue("$deleteCount", deleteCount);
                deleteCommand.ExecuteNonQuery();
                trimmedThreads++;
            }

            return trimmedThreads;
        });
    }

    public Task<IReadOnlyList<string>> GetOtherThreadContextAsync(int currentThreadId, int perThreadLimit = 4)
    {
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            var contexts = new List<string>();
            var threads = new List<(int Id, string Title, string Summary)>();
            using var connection = OpenConnection();
            using var threadCommand = connection.CreateCommand();
            threadCommand.CommandText = "SELECT id, title, summary FROM chat_threads WHERE id <> $currentThreadId ORDER BY updated_at DESC LIMIT 20";
            threadCommand.Parameters.AddWithValue("$currentThreadId", currentThreadId);

            using var threadReader = threadCommand.ExecuteReader();
            while (threadReader.Read())
            {
                threads.Add((threadReader.GetInt32(0), threadReader.GetString(1), threadReader.GetString(2)));
            }

            foreach (var thread in threads)
            {
                var lines = new List<string>();

                using var messageCommand = connection.CreateCommand();
                messageCommand.CommandText = """
                    SELECT role, content, timestamp
                    FROM chat_messages
                    WHERE thread_id = $threadId
                    ORDER BY id DESC
                    LIMIT $limit
                    """;
                messageCommand.Parameters.AddWithValue("$threadId", thread.Id);
                messageCommand.Parameters.AddWithValue("$limit", perThreadLimit);

                using var messageReader = messageCommand.ExecuteReader();
                while (messageReader.Read())
                {
                    lines.Add($"[{messageReader.GetString(2)}] {messageReader.GetString(0)}: {messageReader.GetString(1)}");
                }

                lines.Reverse();
                if (!string.IsNullOrWhiteSpace(thread.Summary) || lines.Count > 0)
                {
                    var builder = new StringWriter();
                    builder.WriteLine($"Thread: {thread.Title}");
                    if (!string.IsNullOrWhiteSpace(thread.Summary))
                    {
                        builder.WriteLine($"Summary: {thread.Summary}");
                    }

                    if (lines.Count > 0)
                    {
                        builder.WriteLine("Recent tail:");
                        builder.Write(string.Join("\n", lines));
                    }

                    contexts.Add(builder.ToString().TrimEnd());
                }
            }

            return contexts;
        });
    }

    public Task<string> GetThreadContextAsync(int threadId, int messageLimit = 12)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var threadCommand = connection.CreateCommand();
            threadCommand.CommandText = "SELECT title, summary FROM chat_threads WHERE id = $threadId";
            threadCommand.Parameters.AddWithValue("$threadId", threadId);

            using var threadReader = threadCommand.ExecuteReader();
            if (!threadReader.Read())
            {
                return string.Empty;
            }

            var title = threadReader.GetString(0);
            var summary = threadReader.GetString(1);
            threadReader.Close();

            var lines = new List<string>();
            using var messageCommand = connection.CreateCommand();
            messageCommand.CommandText = """
                SELECT role, content, timestamp
                FROM chat_messages
                WHERE thread_id = $threadId
                ORDER BY id DESC
                LIMIT $limit
                """;
            messageCommand.Parameters.AddWithValue("$threadId", threadId);
            messageCommand.Parameters.AddWithValue("$limit", messageLimit);

            using var messageReader = messageCommand.ExecuteReader();
            while (messageReader.Read())
            {
                lines.Add($"[{messageReader.GetString(2)}] {messageReader.GetString(0)}: {messageReader.GetString(1)}");
            }

            lines.Reverse();
            if (string.IsNullOrWhiteSpace(summary) && lines.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringWriter();
            builder.WriteLine($"Thread: {title}");
            if (!string.IsNullOrWhiteSpace(summary))
            {
                builder.WriteLine($"Summary: {summary}");
            }

            if (lines.Count > 0)
            {
                builder.WriteLine("Recent tail:");
                builder.Write(string.Join("\n", lines));
            }

            return builder.ToString().TrimEnd();
        });
    }

    public Task<IReadOnlyList<MemoryItem>> GetMemoryAsync()
    {
        return Task.Run<IReadOnlyList<MemoryItem>>(() =>
        {
            var items = new List<MemoryItem>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value, updated_at, type, source FROM memory_items ORDER BY key";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new MemoryItem
                {
                    Key = reader.GetString(0),
                    Value = reader.GetString(1),
                    UpdatedAt = reader.GetString(2),
                    Type = reader.GetString(3),
                    Source = reader.GetString(4)
                });
            }

            return items;
        });
    }

    public Task<string?> GetMemoryValueAsync(string key)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM memory_items WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        });
    }

    public Task<IReadOnlyList<MemoryTransaction>> GetMemoryTransactionsAsync(int limit = 80)
    {
        return Task.Run<IReadOnlyList<MemoryTransaction>>(() =>
        {
            var transactions = new List<MemoryTransaction>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, timestamp, action, field, value, status, source, error, type
                FROM memory_transactions
                ORDER BY id DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                transactions.Add(new MemoryTransaction
                {
                    Id = reader.GetInt32(0),
                    Timestamp = reader.GetString(1),
                    Action = reader.GetString(2),
                    Field = reader.GetString(3),
                    Value = reader.GetString(4),
                    Status = reader.GetString(5),
                    Source = reader.GetString(6),
                    Error = reader.GetString(7),
                    Type = reader.GetString(8)
                });
            }

            return transactions;
        });
    }

    public Task<int> AddMemoryTransactionAsync(string action, string field, string value, string status, string source, string error = "", string type = "fact")
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO memory_transactions(timestamp, action, field, value, status, source, error, type)
                VALUES ($timestamp, $action, $field, $value, $status, $source, $error, $type)
                """;
            command.Parameters.AddWithValue("$timestamp", DateTimeOffset.Now.ToString("O"));
            command.Parameters.AddWithValue("$action", action);
            command.Parameters.AddWithValue("$field", field);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$error", error);
            command.Parameters.AddWithValue("$type", type);
            command.ExecuteNonQuery();

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid()";
            return Convert.ToInt32(idCommand.ExecuteScalar());
        });
    }

    public Task UpdateMemoryTransactionStatusAsync(int id, string status, string error = "")
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE memory_transactions SET status = $status, error = $error WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$error", error);
            command.ExecuteNonQuery();
        });
    }

    public Task<IReadOnlyList<CalendarCandidate>> GetCalendarCandidatesAsync(int limit = 80)
    {
        return Task.Run<IReadOnlyList<CalendarCandidate>>(() =>
        {
            var candidates = new List<CalendarCandidate>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, timestamp, title, starts_at, details, status, source, error
                FROM calendar_candidates
                ORDER BY id DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add(new CalendarCandidate
                {
                    Id = reader.GetInt32(0),
                    Timestamp = reader.GetString(1),
                    Title = reader.GetString(2),
                    StartsAt = reader.GetString(3),
                    Details = reader.GetString(4),
                    Status = reader.GetString(5),
                    Source = reader.GetString(6),
                    Error = reader.GetString(7)
                });
            }

            return candidates;
        });
    }

    public Task<int> AddCalendarCandidateAsync(string title, string startsAt, string details, string status, string source, string error = "")
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO calendar_candidates(timestamp, title, starts_at, details, status, source, error)
                VALUES ($timestamp, $title, $startsAt, $details, $status, $source, $error)
                """;
            command.Parameters.AddWithValue("$timestamp", DateTimeOffset.Now.ToString("O"));
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$startsAt", startsAt);
            command.Parameters.AddWithValue("$details", details);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$error", error);
            command.ExecuteNonQuery();

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid()";
            return Convert.ToInt32(idCommand.ExecuteScalar());
        });
    }

    public Task UpdateCalendarCandidateStatusAsync(int id, string status, string error = "")
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE calendar_candidates SET status = $status, error = $error WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$error", error);
            command.ExecuteNonQuery();
        });
    }

    public Task<string?> GetRecentSuccessfulMemoryValueAsync(string key, DateTimeOffset since)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT value
                FROM memory_transactions
                WHERE field = $field AND status = 'success' AND timestamp >= $since
                ORDER BY id DESC
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$field", key);
            command.Parameters.AddWithValue("$since", since.ToString("O"));
            return command.ExecuteScalar() as string;
        });
    }

    public Task UpsertMemoryAsync(string key, string value, string type = "fact", string source = "chat")
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO memory_items(key, value, updated_at, type, source, source_at)
                VALUES ($key, $value, $updatedAt, $type, $source, $updatedAt)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at, type = excluded.type,
                    source = excluded.source, source_at = excluded.source_at
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$source", source);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Who last set this memory value ("manual" or "chat") and when. Null if the key is unset.</summary>
    public Task<(string Value, string Source, string SourceAt)?> GetMemoryProvenanceAsync(string key)
    {
        return Task.Run<(string Value, string Source, string SourceAt)?>(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value, source, source_at FROM memory_items WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
        });
    }

    public Task<int> AddReviewItemAsync(string kind, string targetKey, string label, string currentValue, string proposedValue, string evidence)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();

            // One proposal per target and value, open or already dismissed: a repeated claim is not a new conflict.
            using var existing = connection.CreateCommand();
            existing.CommandText = "SELECT id FROM review_items WHERE status IN ('open', 'dismissed') AND kind = $kind AND target_key = $target AND proposed_value = $proposed";
            existing.Parameters.AddWithValue("$kind", kind);
            existing.Parameters.AddWithValue("$target", targetKey);
            existing.Parameters.AddWithValue("$proposed", proposedValue);
            if (existing.ExecuteScalar() is long existingId)
            {
                return (int)existingId;
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO review_items(kind, target_key, label, current_value, proposed_value, evidence, status, created_at)
                VALUES ($kind, $target, $label, $current, $proposed, $evidence, 'open', $createdAt);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$target", targetKey);
            insert.Parameters.AddWithValue("$label", label);
            insert.Parameters.AddWithValue("$current", currentValue);
            insert.Parameters.AddWithValue("$proposed", proposedValue);
            insert.Parameters.AddWithValue("$evidence", evidence);
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("O"));
            return Convert.ToInt32(insert.ExecuteScalar());
        });
    }

    public Task<IReadOnlyList<ReviewItem>> GetOpenReviewItemsAsync()
    {
        return Task.Run<IReadOnlyList<ReviewItem>>(() =>
        {
            var items = new List<ReviewItem>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, kind, target_key, label, current_value, proposed_value, evidence, created_at FROM review_items WHERE status = 'open' ORDER BY id DESC";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new ReviewItem
                {
                    Id = reader.GetInt32(0),
                    Kind = reader.GetString(1),
                    TargetKey = reader.GetString(2),
                    Label = reader.GetString(3),
                    CurrentValue = reader.GetString(4),
                    ProposedValue = reader.GetString(5),
                    Evidence = reader.GetString(6),
                    CreatedAt = reader.GetString(7)
                });
            }

            return items;
        });
    }

    public Task ResolveReviewItemAsync(int id, string status)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE review_items SET status = $status, resolved_at = $resolvedAt WHERE id = $id";
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$resolvedAt", DateTimeOffset.Now.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Table and column names only (never row data): used by the self_docs skill.</summary>
    public Task<string> GetSchemaSummaryAsync()
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            var tables = new List<string>();
            using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                using var reader = list.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            var sb = new System.Text.StringBuilder("Database tables and columns (encrypted with SQLCipher):\n");
            foreach (var table in tables)
            {
                var columns = new List<string>();
                using var info = connection.CreateCommand();
                info.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                {
                    columns.Add($"{reader.GetString(1)} {reader.GetString(2).ToLowerInvariant()}");
                }

                sb.AppendLine($"- {table}: {string.Join(", ", columns)}");
            }

            return sb.ToString().TrimEnd();
        });
    }

    // ---- corrections ledger (permanent, tiny) --------------------------------------------------

    public Task AddCorrectionAsync(string kind, string target, string oldValue, string newValue, string source, string note = "")
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO corrections(ts, kind, target, old_value, new_value, source, note)
                VALUES ($ts, $kind, $target, $old, $new, $source, $note)
                """;
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$target", target);
            command.Parameters.AddWithValue("$old", oldValue);
            command.Parameters.AddWithValue("$new", newValue);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$note", note);
            command.ExecuteNonQuery();
        });
    }

    public Task<IReadOnlyList<Correction>> GetCorrectionsAsync(int limit = 20)
    {
        return Task.Run<IReadOnlyList<Correction>>(() =>
        {
            var items = new List<Correction>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ts, kind, target, old_value, new_value, source, note FROM corrections ORDER BY id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new Correction(
                    DateTimeOffset.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
            }

            return items;
        });
    }

    // ---- runtime event log ---------------------------------------------------------------------

    public Task AddRuntimeEventsAsync(IReadOnlyList<RuntimeEvent> events, int keep)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var e in events)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO runtime_events(ts, component, type, outcome, latency_ms, model, tokens_in, tokens_out, detail)
                    VALUES ($ts, $component, $type, $outcome, $latency, $model, $tin, $tout, $detail)
                    """;
                insert.Parameters.AddWithValue("$ts", e.Time.UtcDateTime.ToString("O"));
                insert.Parameters.AddWithValue("$component", e.Component);
                insert.Parameters.AddWithValue("$type", e.Type);
                insert.Parameters.AddWithValue("$outcome", e.Outcome);
                insert.Parameters.AddWithValue("$latency", (object?)e.LatencyMs ?? DBNull.Value);
                insert.Parameters.AddWithValue("$model", (object?)e.Model ?? DBNull.Value);
                insert.Parameters.AddWithValue("$tin", (object?)e.TokensIn ?? DBNull.Value);
                insert.Parameters.AddWithValue("$tout", (object?)e.TokensOut ?? DBNull.Value);
                insert.Parameters.AddWithValue("$detail", e.Detail);
                insert.ExecuteNonQuery();
            }

            // keep only the newest rows so the log stays small and fast
            using var trim = connection.CreateCommand();
            trim.Transaction = transaction;
            trim.CommandText = "DELETE FROM runtime_events WHERE id <= (SELECT MAX(id) FROM runtime_events) - $keep";
            trim.Parameters.AddWithValue("$keep", keep);
            trim.ExecuteNonQuery();

            transaction.Commit();
        });
    }

    public Task<RuntimeQueryResult> QueryRuntimeEventsAsync(RuntimeQuery query)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();

            var where = new List<string>();
            var parameters = new List<(string, object)>();
            if (query.Since is { } since)
            {
                where.Add("ts >= $since");
                parameters.Add(("$since", since.UtcDateTime.ToString("O")));
            }

            if (!string.IsNullOrWhiteSpace(query.Component))
            {
                where.Add("component = $component COLLATE NOCASE");
                parameters.Add(("$component", query.Component.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(query.Type))
            {
                where.Add("type = $type COLLATE NOCASE");
                parameters.Add(("$type", query.Type.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(query.Outcome))
            {
                where.Add("outcome = $outcome COLLATE NOCASE");
                parameters.Add(("$outcome", query.Outcome.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(query.Contains))
            {
                var like = "%" + query.Contains.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                where.Add("(detail LIKE $like ESCAPE '\\' OR type LIKE $like ESCAPE '\\' OR component LIKE $like ESCAPE '\\')");
                parameters.Add(("$like", like));
            }

            var clause = where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where);

            void Bind(SqliteCommand command)
            {
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }
            }

            // aggregates over everything that matches
            int total = 0, errors = 0, llmCalls = 0;
            long tokensIn = 0, tokensOut = 0;
            double llmMsSum = 0;
            DateTimeOffset? earliest = null, latest = null;
            var byComponent = new Dictionary<string, int>();
            var models = new SortedSet<string>();

            using (var aggregate = connection.CreateCommand())
            {
                aggregate.CommandText = $"SELECT component, outcome, COUNT(*), COALESCE(SUM(tokens_in),0), COALESCE(SUM(tokens_out),0), COALESCE(SUM(latency_ms),0), MIN(ts), MAX(ts) FROM runtime_events{clause} GROUP BY component, outcome";
                Bind(aggregate);
                using var reader = aggregate.ExecuteReader();
                while (reader.Read())
                {
                    var component = reader.GetString(0);
                    var outcome = reader.GetString(1);
                    var count = reader.GetInt32(2);
                    total += count;
                    if (outcome == "error")
                    {
                        errors += count;
                    }

                    byComponent[component] = byComponent.GetValueOrDefault(component) + count;
                    if (component == "llm")
                    {
                        llmCalls += count;
                        tokensIn += reader.GetInt64(3);
                        tokensOut += reader.GetInt64(4);
                        llmMsSum += reader.GetInt64(5);
                    }

                    if (DateTimeOffset.TryParse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind, out var lo) && (earliest is null || lo < earliest))
                    {
                        earliest = lo;
                    }

                    if (DateTimeOffset.TryParse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind, out var hi) && (latest is null || hi > latest))
                    {
                        latest = hi;
                    }
                }
            }

            using (var modelQuery = connection.CreateCommand())
            {
                modelQuery.CommandText = $"SELECT DISTINCT model FROM runtime_events{(clause.Length == 0 ? " WHERE" : clause + " AND")} model IS NOT NULL";
                Bind(modelQuery);
                using var reader = modelQuery.ExecuteReader();
                while (reader.Read())
                {
                    models.Add(reader.GetString(0));
                }
            }

            var events = new List<RuntimeEvent>();
            using (var rows = connection.CreateCommand())
            {
                rows.CommandText = $"SELECT id, ts, component, type, outcome, latency_ms, model, tokens_in, tokens_out, detail FROM runtime_events{clause} ORDER BY id DESC LIMIT $limit";
                Bind(rows);
                rows.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 100));
                using var reader = rows.ExecuteReader();
                while (reader.Read())
                {
                    events.Add(new RuntimeEvent(
                        DateTimeOffset.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(9),
                        reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        reader.IsDBNull(8) ? null : reader.GetInt32(8)) { Id = reader.GetInt64(0) });
                }
            }

            return new RuntimeQueryResult(events, total, errors, byComponent, llmCalls, tokensIn, tokensOut,
                llmCalls == 0 ? 0 : llmMsSum / llmCalls, models.ToList(), earliest, latest);
        });
    }

    public Task<IReadOnlyList<MemoryItem>> GetMemoryByPrefixAsync(string prefix)
    {
        return Task.Run<IReadOnlyList<MemoryItem>>(() =>
        {
            var items = new List<MemoryItem>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value, updated_at, type FROM memory_items WHERE key LIKE $prefix ORDER BY key";
            command.Parameters.AddWithValue("$prefix", prefix + "%");

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new MemoryItem
                {
                    Key = reader.GetString(0),
                    Value = reader.GetString(1),
                    UpdatedAt = reader.GetString(2),
                    Type = reader.GetString(3)
                });
            }

            return items;
        });
    }

    public Task<IReadOnlyList<MemoryItem>> SearchMemoryAsync(string term)
    {
        return Task.Run<IReadOnlyList<MemoryItem>>(() =>
        {
            var items = new List<MemoryItem>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value, updated_at, type FROM memory_items WHERE key LIKE $term OR value LIKE $term ORDER BY key";
            command.Parameters.AddWithValue("$term", "%" + term + "%");

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new MemoryItem
                {
                    Key = reader.GetString(0),
                    Value = reader.GetString(1),
                    UpdatedAt = reader.GetString(2),
                    Type = reader.GetString(3)
                });
            }

            return items;
        });
    }

    public Task RenameMemoryKeyAsync(string oldKey, string newKey)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using var readCommand = connection.CreateCommand();
            readCommand.Transaction = transaction;
            readCommand.CommandText = "SELECT value, updated_at, source, source_at FROM memory_items WHERE key = $oldKey";
            readCommand.Parameters.AddWithValue("$oldKey", oldKey);

            using var reader = readCommand.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Rollback();
                return;
            }

            var value = reader.GetString(0);
            var updatedAt = reader.GetString(1);
            var source = reader.GetString(2);
            var sourceAt = reader.GetString(3);
            reader.Close();

            var category = newKey.Contains(':') ? newKey.Split(':')[0] : "flex";
            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO memory_items(key, value, updated_at, type, source, source_at)
                VALUES ($key, $value, $updatedAt, $type, $source, $sourceAt)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at,
                    source = excluded.source, source_at = excluded.source_at
                """;
            insertCommand.Parameters.AddWithValue("$key", newKey);
            insertCommand.Parameters.AddWithValue("$value", value);
            insertCommand.Parameters.AddWithValue("$updatedAt", updatedAt);
            insertCommand.Parameters.AddWithValue("$type", category);
            insertCommand.Parameters.AddWithValue("$source", source);
            insertCommand.Parameters.AddWithValue("$sourceAt", sourceAt);
            insertCommand.ExecuteNonQuery();

            using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM memory_items WHERE key = $oldKey";
            deleteCommand.Parameters.AddWithValue("$oldKey", oldKey);
            deleteCommand.ExecuteNonQuery();

            transaction.Commit();
        });
    }

    public Task MergeMemoryKeysAsync(string targetKey, string sourceKey)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using var targetCommand = connection.CreateCommand();
            targetCommand.Transaction = transaction;
            targetCommand.CommandText = "SELECT value FROM memory_items WHERE key = $key";
            targetCommand.Parameters.AddWithValue("$key", targetKey);
            var targetValue = targetCommand.ExecuteScalar() as string ?? "";

            using var sourceCommand = connection.CreateCommand();
            sourceCommand.Transaction = transaction;
            sourceCommand.CommandText = "SELECT value FROM memory_items WHERE key = $key";
            sourceCommand.Parameters.AddWithValue("$key", sourceKey);
            var sourceValue = sourceCommand.ExecuteScalar() as string;

            if (sourceValue is null)
            {
                transaction.Rollback();
                return;
            }

            var merged = string.IsNullOrWhiteSpace(targetValue) ? sourceValue : $"{targetValue}; {sourceValue}";
            var category = targetKey.Contains(':') ? targetKey.Split(':')[0] : "flex";

            using var upsertCommand = connection.CreateCommand();
            upsertCommand.Transaction = transaction;
            upsertCommand.CommandText = """
                INSERT INTO memory_items(key, value, updated_at, type, source, source_at)
                VALUES ($key, $value, $updatedAt, $type, 'manual', $updatedAt)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at,
                    source = 'manual', source_at = excluded.updated_at
                """;
            upsertCommand.Parameters.AddWithValue("$key", targetKey);
            upsertCommand.Parameters.AddWithValue("$value", merged);
            upsertCommand.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
            upsertCommand.Parameters.AddWithValue("$type", category);
            upsertCommand.ExecuteNonQuery();

            using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM memory_items WHERE key = $key";
            deleteCommand.Parameters.AddWithValue("$key", sourceKey);
            deleteCommand.ExecuteNonQuery();

            transaction.Commit();
        });
    }

    public Task<int> GetNonEmptyMemoryCountAsync()
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM memory_items WHERE value <> ''";
            return Convert.ToInt32(command.ExecuteScalar());
        });
    }

    public Task<int> AddDevNoteAsync(string note, string source = "user")
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO dev_notes(note, source, created_at) VALUES ($note, $source, $createdAt)";
            command.Parameters.AddWithValue("$note", note);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("O"));
            command.ExecuteNonQuery();

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid()";
            return Convert.ToInt32(idCommand.ExecuteScalar());
        });
    }

    public Task<IReadOnlyList<DevNote>> GetDevNotesAsync(string? statusFilter = null)
    {
        return Task.Run<IReadOnlyList<DevNote>>(() =>
        {
            var notes = new List<DevNote>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = statusFilter is null
                ? "SELECT id, note, source, status, created_at, completed_at FROM dev_notes ORDER BY id DESC"
                : "SELECT id, note, source, status, created_at, completed_at FROM dev_notes WHERE status = $status ORDER BY id DESC";
            if (statusFilter is not null)
                command.Parameters.AddWithValue("$status", statusFilter);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                notes.Add(new DevNote
                {
                    Id = reader.GetInt32(0),
                    Note = reader.GetString(1),
                    Source = reader.GetString(2),
                    Status = reader.GetString(3),
                    CreatedAt = reader.GetString(4),
                    CompletedAt = reader.GetString(5)
                });
            }

            return notes;
        });
    }

    public Task CompleteDevNoteAsync(int id)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE dev_notes SET status = 'done', completed_at = $completedAt WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$completedAt", DateTimeOffset.Now.ToString("O"));
            command.ExecuteNonQuery();
        });
    }

    public Task DeleteDevNoteAsync(int id)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM dev_notes WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        });
    }

    public Task DeleteMemoryAsync(string key)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memory_items WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
        });
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS journal_entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                entry_date TEXT NOT NULL,
                title TEXT NOT NULL,
                mood TEXT NOT NULL,
                body TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS calendar_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                starts_at TEXT NOT NULL,
                details TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reminders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                due_at TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending',
                source_event_id INTEGER
            );

            CREATE TABLE IF NOT EXISTS journal_tasks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                is_done INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS chat_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                thread_id INTEGER NOT NULL DEFAULT 1,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                timestamp TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS chat_threads (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                summary TEXT NOT NULL DEFAULT '',
                is_main INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS memory_items (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                type TEXT NOT NULL DEFAULT 'fact'
            );

            CREATE TABLE IF NOT EXISTS memory_transactions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                action TEXT NOT NULL,
                field TEXT NOT NULL,
                value TEXT NOT NULL,
                status TEXT NOT NULL,
                source TEXT NOT NULL,
                error TEXT NOT NULL DEFAULT '',
                type TEXT NOT NULL DEFAULT 'fact'
            );

            CREATE TABLE IF NOT EXISTS dev_notes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                note TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT 'user',
                status TEXT NOT NULL DEFAULT 'open',
                created_at TEXT NOT NULL,
                completed_at TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS calendar_candidates (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                title TEXT NOT NULL,
                starts_at TEXT NOT NULL,
                details TEXT NOT NULL,
                status TEXT NOT NULL,
                source TEXT NOT NULL,
                error TEXT NOT NULL DEFAULT ''
            );
            """;
        command.ExecuteNonQuery();

        EnsureChatSchema(connection);
        EnsureMemorySchema(connection);
        EnsureProvenanceSchema(connection);
        EnsureRuntimeLogSchema(connection);
    }

    private static void EnsureRuntimeLogSchema(SqliteConnection connection)
    {
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS runtime_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                component TEXT NOT NULL,
                type TEXT NOT NULL,
                outcome TEXT NOT NULL,
                latency_ms INTEGER,
                model TEXT,
                tokens_in INTEGER,
                tokens_out INTEGER,
                detail TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS corrections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                kind TEXT NOT NULL,
                target TEXT NOT NULL,
                old_value TEXT NOT NULL DEFAULT '',
                new_value TEXT NOT NULL DEFAULT '',
                source TEXT NOT NULL,
                note TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_runtime_events_ts ON runtime_events(ts);
            CREATE INDEX IF NOT EXISTS ix_runtime_events_component ON runtime_events(component, ts);
            """;
        create.ExecuteNonQuery();
    }

    /// <summary>
    /// Every memory item, calendar event and task remembers who set it last ("manual" = the user in the
    /// UI, "chat" = filed automatically) and when, so automatic processes never silently overwrite a
    /// value the user set by hand. Conflicts go to review_items instead.
    /// </summary>
    private void EnsureProvenanceSchema(SqliteConnection connection)
    {
        if (!ColumnExists(connection, "memory_items", "source"))
        {
            BackupBeforeSchemaChange();
        }

        foreach (var table in new[] { "memory_items", "calendar_events", "journal_tasks" })
        {
            if (!ColumnExists(connection, table, "source"))
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE {table} ADD COLUMN source TEXT NOT NULL DEFAULT 'chat'";
                alter.ExecuteNonQuery();
            }

            if (!ColumnExists(connection, table, "source_at"))
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE {table} ADD COLUMN source_at TEXT NOT NULL DEFAULT ''";
                alter.ExecuteNonQuery();
            }
        }

        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS review_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL,
                target_key TEXT NOT NULL,
                label TEXT NOT NULL DEFAULT '',
                current_value TEXT NOT NULL DEFAULT '',
                proposed_value TEXT NOT NULL DEFAULT '',
                evidence TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT 'open',
                created_at TEXT NOT NULL,
                resolved_at TEXT NOT NULL DEFAULT ''
            );
            """;
        create.ExecuteNonQuery();
    }

    private void BackupBeforeSchemaChange()
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(dbFilePath)!, "backups");
            Directory.CreateDirectory(dir);
            File.Copy(dbFilePath, Path.Combine(dir, $"clio.db.pre-provenance-{DateTime.Now:yyyyMMdd-HHmmss}"), overwrite: false);
        }
        catch
        {
            // Best effort: a failed backup must not stop the app from starting.
        }
    }

    private static void EnsureMemorySchema(SqliteConnection connection)
    {
        if (!ColumnExists(connection, "memory_items", "type"))
        {
            using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE memory_items ADD COLUMN type TEXT NOT NULL DEFAULT 'fact'";
            alterCommand.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, "memory_transactions", "type"))
        {
            using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE memory_transactions ADD COLUMN type TEXT NOT NULL DEFAULT 'fact'";
            alterCommand.ExecuteNonQuery();
        }

        // Set type to category prefix for any migrated keys
        using var fixCommand = connection.CreateCommand();
        fixCommand.CommandText = """
            UPDATE memory_items SET type = SUBSTR(key, 1, INSTR(key, ':') - 1)
            WHERE key LIKE '%:%' AND type IN ('fact', 'preference', 'pattern')
            """;
        fixCommand.ExecuteNonQuery();
    }

    private static void EnsureChatSchema(SqliteConnection connection)
    {
        if (!ColumnExists(connection, "chat_messages", "thread_id"))
        {
            using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE chat_messages ADD COLUMN thread_id INTEGER NOT NULL DEFAULT 1";
            alterCommand.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, "chat_threads", "summary"))
        {
            using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE chat_threads ADD COLUMN summary TEXT NOT NULL DEFAULT ''";
            alterCommand.ExecuteNonQuery();
        }

        using var mainThreadCommand = connection.CreateCommand();
        mainThreadCommand.CommandText = "INSERT OR IGNORE INTO chat_threads(id, title, summary, is_main, updated_at) VALUES (1, 'Main Chat', '', 1, $updatedAt)";
        mainThreadCommand.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        mainThreadCommand.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var tableInfoCommand = connection.CreateCommand();
        tableInfoCommand.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = tableInfoCommand.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Dictionary<string, string> LegacyKeyMigration = new(StringComparer.OrdinalIgnoreCase)
    {
        // user:
        ["name"] = "user:name",
        ["preferred_name"] = "user:preferred_name",
        ["pronouns"] = "user:pronouns",
        ["birthdate"] = "user:birthdate",
        ["current_location"] = "user:location",
        ["nationality"] = "user:nationality",
        ["languages_spoken"] = "user:languages",
        ["education"] = "user:education",
        ["relationship_status"] = "user:relationship_status",
        ["personality_traits"] = "user:traits",
        ["communication_style"] = "user:communication_style",
        ["timezone"] = "user:timezone",
        ["occupation"] = "user:occupation",
        ["living_situation"] = "user:living_situation",
        ["home_address"] = "user:home_address",
        ["work_address"] = "user:work_address",
        ["vehicle"] = "user:vehicle",
        ["employer"] = "user:employer",
        ["work_schedule"] = "user:work_schedule",
        ["hobbies"] = "user:interests",
        ["favorite_foods"] = "user:favorites:foods",
        ["favorite_media"] = "user:favorites:media",
        ["social_media_handles"] = "user:social_media",
        ["skills"] = "user:skills",
        // user:health:
        ["diet_preferences"] = "user:health:diet",
        ["health_conditions"] = "user:health:conditions",
        ["medications"] = "user:health:medications",
        ["allergies"] = "user:health:allergies",
        ["stress_triggers"] = "user:health:stress_triggers",
        ["coping_strategies"] = "user:health:coping_strategies",
        // user:routines:
        ["morning_routine"] = "user:routines:morning",
        ["evening_routine"] = "user:routines:evening",
        ["sleep_schedule"] = "user:routines:sleep",
        ["exercise_routine"] = "user:routines:exercise",
        // people:
        ["partner"] = "people:partner",
        ["children"] = "people:children",
        ["family"] = "people:family",
        ["pets"] = "people:pets",
        ["emergency_contact"] = "people:emergency_contact",
        // projects:
        ["current_project"] = "projects:current",
        // goals:
        ["goals"] = "goals:active",
        ["current_focus"] = "goals:current_focus",
        ["financial_goals"] = "goals:financial",
        ["bucket_list"] = "goals:bucket_list",
        // context:
        ["constraints"] = "context:constraints",
        ["travel_plans"] = "context:travel_plans",
        ["life_events"] = "context:life_events",
        ["anniversary_date"] = "context:anniversary_date",
        // systems:
        ["tech_setup"] = "systems:tech_setup",
        // meta:
        ["preferred_reflection_style"] = "meta:reflection_style",
    };

    private void MigrateToHierarchicalKeys()
    {
        using var connection = OpenConnection();

        // Check if migration already ran (any key contains a colon)
        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT COUNT(*) FROM memory_items WHERE key LIKE '%:%'";
        var colonCount = Convert.ToInt32(checkCommand.ExecuteScalar());

        using var legacyCheck = connection.CreateCommand();
        legacyCheck.CommandText = "SELECT COUNT(*) FROM memory_items WHERE key NOT LIKE '%:%'";
        var legacyCount = Convert.ToInt32(legacyCheck.ExecuteScalar());

        if (legacyCount == 0)
            return;

        using var transaction = connection.BeginTransaction();

        // Migrate known flat keys
        foreach (var (oldKey, newKey) in LegacyKeyMigration)
        {
            using var readCommand = connection.CreateCommand();
            readCommand.Transaction = transaction;
            readCommand.CommandText = "SELECT value, updated_at, type FROM memory_items WHERE key = $oldKey";
            readCommand.Parameters.AddWithValue("$oldKey", oldKey);

            using var reader = readCommand.ExecuteReader();
            if (!reader.Read())
                continue;

            var value = reader.GetString(0);
            var updatedAt = reader.GetString(1);
            reader.Close();

            using var upsertCommand = connection.CreateCommand();
            upsertCommand.Transaction = transaction;
            upsertCommand.CommandText = """
                INSERT INTO memory_items(key, value, updated_at, type)
                VALUES ($key, $value, $updatedAt, $type)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
                """;
            upsertCommand.Parameters.AddWithValue("$key", newKey);
            upsertCommand.Parameters.AddWithValue("$value", value);
            upsertCommand.Parameters.AddWithValue("$updatedAt", updatedAt);
            var category = newKey.Split(':')[0];
            upsertCommand.Parameters.AddWithValue("$type", category);
            upsertCommand.ExecuteNonQuery();

            using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM memory_items WHERE key = $oldKey";
            deleteCommand.Parameters.AddWithValue("$oldKey", oldKey);
            deleteCommand.ExecuteNonQuery();
        }

        // Migrate preference_* and pattern_* keys
        var dynamicKeys = new List<(string OldKey, string Value, string UpdatedAt)>();
        using var dynCommand = connection.CreateCommand();
        dynCommand.Transaction = transaction;
        dynCommand.CommandText = "SELECT key, value, updated_at FROM memory_items WHERE key LIKE 'preference_%' OR key LIKE 'pattern_%'";
        using var dynReader = dynCommand.ExecuteReader();
        while (dynReader.Read())
            dynamicKeys.Add((dynReader.GetString(0), dynReader.GetString(1), dynReader.GetString(2)));
        dynReader.Close();

        foreach (var (oldKey, value, updatedAt) in dynamicKeys)
        {
            string newKey;
            string category;
            if (oldKey.StartsWith("preference_"))
            {
                var slug = oldKey["preference_".Length..];
                newKey = $"flex:preference:{slug}";
                category = "flex";
            }
            else
            {
                var slug = oldKey["pattern_".Length..];
                newKey = $"flex:pattern:{slug}";
                category = "flex";
            }

            using var upsertCommand = connection.CreateCommand();
            upsertCommand.Transaction = transaction;
            upsertCommand.CommandText = """
                INSERT INTO memory_items(key, value, updated_at, type)
                VALUES ($key, $value, $updatedAt, $type)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
                """;
            upsertCommand.Parameters.AddWithValue("$key", newKey);
            upsertCommand.Parameters.AddWithValue("$value", value);
            upsertCommand.Parameters.AddWithValue("$updatedAt", updatedAt);
            upsertCommand.Parameters.AddWithValue("$type", category);
            upsertCommand.ExecuteNonQuery();

            using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM memory_items WHERE key = $oldKey";
            deleteCommand.Parameters.AddWithValue("$oldKey", oldKey);
            deleteCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var keyCmd = connection.CreateCommand();
        keyCmd.CommandText = $"PRAGMA key = \"x'{dataProtection.MasterKeyHex}'\"";
        keyCmd.ExecuteNonQuery();

        return connection;
    }
}
