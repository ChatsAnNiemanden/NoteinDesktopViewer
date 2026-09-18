using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace NoteinDesktopViewer;

/// <summary>
/// Persists sync state so we know which files have already been downloaded
/// and can skip re-downloading unchanged files.
/// Stored at %LOCALAPPDATA%\NoteinDesktopViewer\sync_metadata.json
/// </summary>
public class SyncMetadata
{
    private static readonly string MetadataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteinDesktopViewer");

    private static readonly string MetadataPath = Path.Combine(MetadataDir, "sync_metadata.json");

    /// <summary>
    /// Maps Drive file ID → last known modified time (UTC) at time of download.
    /// </summary>
    public Dictionary<string, SyncEntry> Files { get; set; } = new();

    public class SyncEntry
    {
        public string Name { get; set; } = string.Empty;
        public DateTime ModifiedTime { get; set; }
    }

    public static async Task<SyncMetadata> LoadAsync()
    {
        if (!File.Exists(MetadataPath))
            return new SyncMetadata();

        try
        {
            var json = await File.ReadAllTextAsync(MetadataPath);
            return JsonSerializer.Deserialize<SyncMetadata>(json) ?? new SyncMetadata();
        }
        catch
        {
            return new SyncMetadata();
        }
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(MetadataDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(MetadataPath, json);
    }

    /// <summary>
    /// Returns true if the file needs to be downloaded (new or modified).
    /// </summary>
    public bool NeedsDownload(string fileId, DateTime? remoteModifiedTime)
    {
        if (remoteModifiedTime == null)
            return true;

        if (!Files.TryGetValue(fileId, out var entry))
            return true; // new file

        return remoteModifiedTime.Value.ToUniversalTime() > entry.ModifiedTime.ToUniversalTime();
    }

    public void UpdateEntry(string fileId, string name, DateTime modifiedTime)
    {
        Files[fileId] = new SyncEntry
        {
            Name = name,
            ModifiedTime = modifiedTime.ToUniversalTime()
        };
    }

    public static void DeleteMetadata()
    {
        if (File.Exists(MetadataPath))
            File.Delete(MetadataPath);
    }
}
