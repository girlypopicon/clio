using CLIo.Models;
using Microsoft.Data.Sqlite;

namespace CLIo.Services;

public sealed class JournalDatabaseService
{
    private readonly string connectionString;
    private readonly DataProtectionService dataProtection;

    public JournalDatabaseService(DataProtectionService dataProtection)
    {
        this.dataProtection = dataProtection;
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLIo");
        Directory.CreateDirectory(appData);
        var dbPath = Path.Combine(appData, "clio.db");
        connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();

        SQLitePCL.Batteries_V2.Init();
        MigrateToEncryptedDatabase(dbPath);
        Initialize();
        MigrateToHierarchicalKeys();
    }

    private void MigrateToEncryptedDatabase(string dbPath)
    {
        if (!File.Exists(dbPath))
            return;

        // Try opening without a key to check if unencrypted
        try
        {
            using var testConn = new SqliteConnection(connectionString);
            testConn.Open();
            using var testCmd = testConn.CreateCommand();
            testCmd.CommandText = "SELECT count(*) FROM sqlite_master";
            testCmd.ExecuteScalar();
        }
        catch
        {
            // Already encrypted or corrupt — skip
            return;
        }

        // DB is unencrypted — encrypt it in place using sqlcipher's rekey
        try
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            using var rekeyCmd = conn.CreateCommand();
            rekeyCmd.CommandText = $"PRAGMA rekey = \"x'{dataProtection.MasterKeyHex}'\"";
            rekeyCmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // rekey failed — DB remains unencrypted, app still works
        }
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
            command.CommandText = "SELECT id, title, starts_at, details FROM calendar_events ORDER BY starts_at LIMIT 500";

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
                    Details = reader.GetString(3)
                });
            }

            return events;
        });
    }

    public Task AddEventAsync(string title, DateTimeOffset startsAt, string details)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO calendar_events(title, starts_at, details) VALUES ($title, $startsAt, $details)";
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$startsAt", startsAt.ToString("O"));
            command.Parameters.AddWithValue("$details", details);
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

    public Task UpdateEventAsync(int id, string title, DateTimeOffset startsAt, string details)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE calendar_events SET title = $title, starts_at = $startsAt, details = $details WHERE id = $id";
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$startsAt", startsAt.ToString("O"));
            command.Parameters.AddWithValue("$details", details);
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

    public Task AddTaskAsync(string title)
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO journal_tasks(title, is_done) VALUES ($title, 0)";
            command.Parameters.AddWithValue("$title", title);
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
            command.CommandText = "SELECT key, value, updated_at, type FROM memory_items ORDER BY key";

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

    public Task UpsertMemoryAsync(string key, string value, string type = "fact")
    {
        return Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO memory_items(key, value, updated_at, type)
                VALUES ($key, $value, $updatedAt, $type)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at, type = excluded.type
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
            command.Parameters.AddWithValue("$type", type);
            command.ExecuteNonQuery();
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
            readCommand.CommandText = "SELECT value, updated_at FROM memory_items WHERE key = $oldKey";
            readCommand.Parameters.AddWithValue("$oldKey", oldKey);

            using var reader = readCommand.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Rollback();
                return;
            }

            var value = reader.GetString(0);
            var updatedAt = reader.GetString(1);
            reader.Close();

            var category = newKey.Contains(':') ? newKey.Split(':')[0] : "flex";
            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO memory_items(key, value, updated_at, type)
                VALUES ($key, $value, $updatedAt, $type)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
                """;
            insertCommand.Parameters.AddWithValue("$key", newKey);
            insertCommand.Parameters.AddWithValue("$value", value);
            insertCommand.Parameters.AddWithValue("$updatedAt", updatedAt);
            insertCommand.Parameters.AddWithValue("$type", category);
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
                INSERT INTO memory_items(key, value, updated_at, type)
                VALUES ($key, $value, $updatedAt, $type)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
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
