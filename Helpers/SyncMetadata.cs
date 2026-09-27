using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace NoteinDesktopViewer.Helpers;

/// <summary>
/// Persists sync state in an SQLite database so we know which files have already been downloaded
/// or converted, and can skip unchanged files.
/// Stored at %LOCALAPPDATA%\NoteinDesktopViewer\sync_metadata.db
/// </summary>
public class SyncMetadata
{
    private static readonly string MetadataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteinDesktopViewer");

    private static readonly string DbPath = Path.Combine(MetadataDir, "sync_metadata.db");
    private static readonly string LegacyJsonPath = Path.Combine(MetadataDir, "sync_metadata.json");
    private static readonly string ConnectionString = $"Data Source={DbPath};";

    /// <summary>
    /// In-memory cache of file ID/path → sync entry for O(1) lookups.
    /// </summary>
    public Dictionary<string, SyncEntry> Files { get; set; } = new();

    public class SyncEntry
    {
        public string FileId { get; set; } = string.Empty;
        public string Source { get; set; } = "GoogleDrive";
        public string Name { get; set; } = string.Empty;
        public DateTime ModifiedTime { get; set; }
    }

    public static async Task<SyncMetadata> LoadAsync()
    {
        var metadata = new SyncMetadata();

        try
        {
            Directory.CreateDirectory(MetadataDir);

            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();

            // Enable WAL mode for high performance and concurrency
            using (var walCmd = conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode = WAL;";
                await walCmd.ExecuteNonQueryAsync();
            }

            // Create table if it doesn't exist
            using (var createCmd = conn.CreateCommand())
            {
                createCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS SyncEntries (
                        FileId TEXT PRIMARY KEY NOT NULL,
                        Source TEXT NOT NULL,
                        Name TEXT NOT NULL,
                        ModifiedTime TEXT NOT NULL
                    );";
                await createCmd.ExecuteNonQueryAsync();
            }

            // Check if we need to migrate from legacy JSON
            await MigrateLegacyJsonIfNeededAsync(conn);

            // Read all entries into in-memory cache
            using (var selectCmd = conn.CreateCommand())
            {
                selectCmd.CommandText = "SELECT FileId, Source, Name, ModifiedTime FROM SyncEntries;";
                using var reader = await selectCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var fileId = reader.GetString(0);
                    var source = reader.GetString(1);
                    var name = reader.GetString(2);
                    var modStr = reader.GetString(3);

                    DateTime modTime = DateTime.TryParse(modStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
                        ? dt.ToUniversalTime()
                        : DateTime.MinValue;

                    metadata.Files[fileId] = new SyncEntry
                    {
                        FileId = fileId,
                        Source = source,
                        Name = name,
                        ModifiedTime = modTime
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error loading sync metadata database: {ex.Message}");
        }

        return metadata;
    }

    private static async Task MigrateLegacyJsonIfNeededAsync(SqliteConnection conn)
    {
        if (!File.Exists(LegacyJsonPath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(LegacyJsonPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Files", out var filesProp))
            {
                using var transaction = conn.BeginTransaction();
                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT OR IGNORE INTO SyncEntries (FileId, Source, Name, ModifiedTime)
                    VALUES (@id, @source, @name, @mod);";

                var pId = insertCmd.Parameters.Add("@id", SqliteType.Text);
                var pSource = insertCmd.Parameters.Add("@source", SqliteType.Text);
                var pName = insertCmd.Parameters.Add("@name", SqliteType.Text);
                var pMod = insertCmd.Parameters.Add("@mod", SqliteType.Text);

                foreach (var item in filesProp.EnumerateObject())
                {
                    var fileId = item.Name;
                    string name = "";
                    DateTime modTime = DateTime.MinValue;

                    if (item.Value.TryGetProperty("Name", out var nameProp))
                        name = nameProp.GetString() ?? "";

                    if (item.Value.TryGetProperty("ModifiedTime", out var modProp) && modProp.TryGetDateTime(out var dt))
                        modTime = dt.ToUniversalTime();

                    pId.Value = fileId;
                    pSource.Value = "GoogleDrive";
                    pName.Value = name;
                    pMod.Value = modTime.ToString("o");

                    await insertCmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }

            var backupPath = LegacyJsonPath + ".bak";
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            File.Move(LegacyJsonPath, backupPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to migrate legacy sync_metadata.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true if the file is new or if remote/local modified time is newer than recorded.
    /// </summary>
    public bool HasChanged(string fileId, DateTime? modifiedTime)
    {
        if (modifiedTime == null)
            return true;

        if (!Files.TryGetValue(fileId, out var entry))
            return true; // new file

        return modifiedTime.Value.ToUniversalTime() > entry.ModifiedTime.ToUniversalTime();
    }

    /// <summary>
    /// Backward-compatible alias for HasChanged.
    /// </summary>
    public bool NeedsDownload(string fileId, DateTime? remoteModifiedTime) => HasChanged(fileId, remoteModifiedTime);

    /// <summary>
    /// Asynchronously updates an entry in-memory and immediately commits it to the SQLite database.
    /// </summary>
    public async Task UpdateEntryAsync(string fileId, string name, DateTime modifiedTime, string source = "GoogleDrive")
    {
        var utcTime = modifiedTime.ToUniversalTime();
        Files[fileId] = new SyncEntry
        {
            FileId = fileId,
            Source = source,
            Name = name,
            ModifiedTime = utcTime
        };

        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO SyncEntries (FileId, Source, Name, ModifiedTime)
                VALUES (@id, @source, @name, @mod)
                ON CONFLICT(FileId) DO UPDATE SET
                    Source = excluded.Source,
                    Name = excluded.Name,
                    ModifiedTime = excluded.ModifiedTime;";

            cmd.Parameters.AddWithValue("@id", fileId);
            cmd.Parameters.AddWithValue("@source", source);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@mod", utcTime.ToString("o"));

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error writing sync entry for {fileId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Synchronously updates an entry (for backward compatibility).
    /// </summary>
    public void UpdateEntry(string fileId, string name, DateTime modifiedTime, string source = "GoogleDrive")
    {
        UpdateEntryAsync(fileId, name, modifiedTime, source).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Maintained for backward compatibility; in SQLite, updates are committed immediately.
    /// </summary>
    public Task SaveAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes the metadata database and legacy files.
    /// </summary>
    public static void DeleteMetadata()
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(DbPath))
            try { File.Delete(DbPath); } catch { }

        var walPath = DbPath + "-wal";
        if (File.Exists(walPath))
            try { File.Delete(walPath); } catch { }

        var shmPath = DbPath + "-shm";
        if (File.Exists(shmPath))
            try { File.Delete(shmPath); } catch { }

        if (File.Exists(LegacyJsonPath))
            try { File.Delete(LegacyJsonPath); } catch { }
    }
}
