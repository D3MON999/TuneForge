using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;

namespace TuneForge.Engine.Core;

public sealed record JournalEntry(
    long Id,
    string TweakId,
    string Hive,
    string Path,
    string Name,
    bool KeyExisted,
    RegistryValue? OldValue,
    RegistryValue? NewValue,
    string Status);

public sealed record ChangeSetSummary(
    long Id,
    string CreatedUtc,
    string RestorePoint,
    string? UndoneUtc,
    IReadOnlyList<string> TweakIds);

/// <summary>Where the journal lives. Only admins can write, so undo can't be fed altered values.</summary>
public static class JournalLocation
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "TuneForge");

    public static string DatabasePath => Path.Combine(Folder, "journal.db");

    public static string DnsBackupPath => Path.Combine(Folder, "dns-backup.json");

    /// <summary>Creates the folder with admin-only write access. Call only from the elevated engine.</summary>
    public static void EnsureProtected()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var (sid, rights) in new[]
                 {
                     (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
                     (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
                     (WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                rights,
                inherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        var folder = new DirectoryInfo(Folder);
        if (!folder.Exists)
        {
            folder.Create(security);
        }

        folder.SetAccessControl(security);
    }
}

/// <summary>Records every change before it's made, so each one can be undone.</summary>
public sealed class Journal : IDisposable
{
    private readonly SqliteConnection _connection;

    private Journal(SqliteConnection connection) => _connection = connection;

    public static Journal OpenForWriting(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();

        var journal = new Journal(connection);
        journal.Execute("""
            PRAGMA journal_mode = DELETE;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS change_set (
              id INTEGER PRIMARY KEY,
              created_utc TEXT NOT NULL,
              user_sid TEXT NOT NULL,
              os_build INTEGER NOT NULL,
              app_version TEXT NOT NULL,
              restore_point TEXT NOT NULL,
              undone_utc TEXT
            );
            CREATE TABLE IF NOT EXISTS change_item (
              id INTEGER PRIMARY KEY,
              change_set_id INTEGER NOT NULL REFERENCES change_set(id),
              tweak_id TEXT NOT NULL,
              hive TEXT NOT NULL,
              path TEXT NOT NULL,
              name TEXT NOT NULL,
              key_existed INTEGER NOT NULL,
              old_kind TEXT,
              old_data TEXT,
              new_kind TEXT,
              new_data TEXT,
              status TEXT NOT NULL,
              error TEXT,
              undone_utc TEXT
            );
            """);
        return journal;
    }

    /// <summary>Opens for history only. Returns null until the first change has been made.</summary>
    public static Journal? OpenForReading(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return new Journal(connection);
    }

    public long BeginChangeSet(string userSid, int osBuild, string appVersion, string restorePoint)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO change_set (created_utc, user_sid, os_build, app_version, restore_point)
            VALUES ($created, $sid, $build, $version, $restore);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$created", Now());
        command.Parameters.AddWithValue("$sid", userSid);
        command.Parameters.AddWithValue("$build", osBuild);
        command.Parameters.AddWithValue("$version", appVersion);
        command.Parameters.AddWithValue("$restore", restorePoint);
        return (long)command.ExecuteScalar()!;
    }

    public long RecordPending(
        long changeSetId,
        string tweakId,
        string hive,
        string path,
        string name,
        bool keyExisted,
        RegistryValue? oldValue,
        RegistryValue? newValue)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO change_item
              (change_set_id, tweak_id, hive, path, name, key_existed, old_kind, old_data, new_kind, new_data, status)
            VALUES ($set, $tweak, $hive, $path, $name, $existed, $oldKind, $oldData, $newKind, $newData, 'pending');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$set", changeSetId);
        command.Parameters.AddWithValue("$tweak", tweakId);
        command.Parameters.AddWithValue("$hive", hive);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$existed", keyExisted ? 1 : 0);
        command.Parameters.AddWithValue("$oldKind", (object?)oldValue?.Kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$oldData", (object?)oldValue?.Data ?? DBNull.Value);
        command.Parameters.AddWithValue("$newKind", (object?)newValue?.Kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$newData", (object?)newValue?.Data ?? DBNull.Value);
        return (long)command.ExecuteScalar()!;
    }

    public void SetStatus(long itemId, string status, string? error = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE change_item SET status = $status, error = $error WHERE id = $id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", itemId);
        command.ExecuteNonQuery();
    }

    public void MarkItemUndone(long itemId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE change_item SET undone_utc = $now WHERE id = $id";
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$id", itemId);
        command.ExecuteNonQuery();
    }

    public void MarkSetUndone(long changeSetId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE change_set SET undone_utc = $now WHERE id = $id";
        command.Parameters.AddWithValue("$now", Now());
        command.Parameters.AddWithValue("$id", changeSetId);
        command.ExecuteNonQuery();
    }

    public string? GetSetUser(long changeSetId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT user_sid FROM change_set WHERE id = $id";
        command.Parameters.AddWithValue("$id", changeSetId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>Changes that may have reached the registry, newest first. "Pending" means it may have.</summary>
    public IReadOnlyList<JournalEntry> GetUndoable(long changeSetId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, tweak_id, hive, path, name, key_existed, old_kind, old_data, new_kind, new_data, status
            FROM change_item
            WHERE change_set_id = $set AND undone_utc IS NULL AND status IN ('pending', 'applied')
            ORDER BY id DESC
            """;
        command.Parameters.AddWithValue("$set", changeSetId);

        var entries = new List<JournalEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new JournalEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5) == 1,
                ReadValue(reader, 6),
                ReadValue(reader, 8),
                reader.GetString(10)));
        }

        return entries;
    }

    public IReadOnlyList<ChangeSetSummary> ListChangeSets(int limit = 100)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.created_utc, s.restore_point, s.undone_utc,
                   (SELECT group_concat(tweak_id, char(10)) FROM
                     (SELECT DISTINCT tweak_id FROM change_item WHERE change_set_id = s.id AND status = 'applied'))
            FROM change_set s
            ORDER BY s.id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var sets = new List<ChangeSetSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var ids = reader.IsDBNull(4) ? [] : reader.GetString(4).Split('\n');
            sets.Add(new ChangeSetSummary(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                ids));
        }

        return sets;
    }

    public void Dispose() => _connection.Dispose();

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static RegistryValue? ReadValue(SqliteDataReader reader, int kindColumn) =>
        reader.IsDBNull(kindColumn) ? null : new RegistryValue(reader.GetString(kindColumn), reader.GetString(kindColumn + 1));

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
