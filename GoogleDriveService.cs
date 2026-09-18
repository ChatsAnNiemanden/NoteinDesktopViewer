using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NoteinDesktopViewer;

public class GoogleDriveService
{
    private static readonly string[] Scopes = { DriveService.Scope.DriveReadonly };
    private const string ApplicationName = "NoteinDesktopViewer";
    private const string TokenFolder = "NoteinDesktopViewer_Tokens";
    private const string TargetFolderName = "NoteInDataSync";

    public static readonly string LocalSyncFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteinDesktopViewer", "NoteInDataSync");

    private UserCredential? _credential;
    private DriveService? _driveService;

    public bool IsLoggedIn => _credential != null;

    /// <summary>
    /// Authenticates the user via Google OAuth2 (opens browser for consent).
    /// Returns the user's email address on success.
    /// </summary>
    public async Task<string> LoginAsync()
    {
        var secretsPath = Path.Combine(AppContext.BaseDirectory, "client_secrets.json");
        if (!File.Exists(secretsPath))
            throw new FileNotFoundException(
                "client_secrets.json not found. Place it next to the executable.", secretsPath);

        using var stream = new FileStream(secretsPath, FileMode.Open, FileAccess.Read);
        var tokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TokenFolder);

        _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            (await GoogleClientSecrets.FromStreamAsync(stream)).Secrets,
            Scopes,
            "user",
            CancellationToken.None,
            new FileDataStore(tokenPath, true));

        _driveService = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = _credential,
            ApplicationName = ApplicationName
        });

        // Fetch user email from the about endpoint
        var aboutRequest = _driveService.About.Get();
        aboutRequest.Fields = "user";
        var about = await aboutRequest.ExecuteAsync();
        return about.User.EmailAddress ?? "Unknown";
    }

    /// <summary>
    /// Lists all files inside the "NoteInDataSync" folder on the user's Drive,
    /// including their modifiedTime for sync comparison.
    /// </summary>
    public async Task<List<DriveFileInfo>> ListNoteInDataSyncFilesAsync()
    {
        if (_driveService == null)
            throw new InvalidOperationException("Not logged in.");

        // 1. Find the "NoteInDataSync" folder
        var folderRequest = _driveService.Files.List();
        folderRequest.Q = $"name = '{TargetFolderName}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        folderRequest.Fields = "files(id, name)";
        var folderResult = await folderRequest.ExecuteAsync();

        var folder = folderResult.Files.FirstOrDefault();
        if (folder == null)
            return new List<DriveFileInfo>(); // folder not found — return empty

        // 2. List files in that folder
        var result = new List<DriveFileInfo>();
        string? pageToken = null;

        do
        {
            var listRequest = _driveService.Files.List();
            listRequest.Q = $"'{folder.Id}' in parents and trashed = false";
            listRequest.Fields = "nextPageToken, files(id, name, modifiedTime)";
            listRequest.PageSize = 100;
            listRequest.PageToken = pageToken;

            var fileResult = await listRequest.ExecuteAsync();
            result.AddRange(fileResult.Files.Select(f => new DriveFileInfo
            {
                Id = f.Id,
                Name = f.Name,
                ModifiedTime = f.ModifiedTimeDateTimeOffset?.DateTime
            }));

            pageToken = fileResult.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return result;
    }

    /// <summary>
    /// Downloads a single file from Drive to the specified local path.
    /// </summary>
    public async Task DownloadFileAsync(string fileId, string destPath)
    {
        if (_driveService == null)
            throw new InvalidOperationException("Not logged in.");

        var dir = Path.GetDirectoryName(destPath);
        if (dir != null)
            Directory.CreateDirectory(dir);

        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write);
        var request = _driveService.Files.Get(fileId);
        await request.DownloadAsync(fileStream);
    }

    /// <summary>
    /// Syncs the NoteInDataSync folder: downloads new/modified files, skips unchanged ones.
    /// Reports progress via the callback (e.g. "Downloading 3/12: notes.db").
    /// Returns the list of all remote files.
    /// </summary>
    public async Task<List<DriveFileInfo>> SyncFilesAsync(IProgress<string>? progress = null)
    {
        progress?.Report("Checking for changes...");

        var remoteFiles = await ListNoteInDataSyncFilesAsync();
        if (remoteFiles.Count == 0)
        {
            progress?.Report("No files found in NoteInDataSync folder.");
            return remoteFiles;
        }

        var metadata = await SyncMetadata.LoadAsync();
        Directory.CreateDirectory(LocalSyncFolder);

        int downloaded = 0;
        int skipped = 0;

        for (int i = 0; i < remoteFiles.Count; i++)
        {
            var file = remoteFiles[i];
            var localPath = Path.Combine(LocalSyncFolder, file.Name);

            if (!metadata.NeedsDownload(file.Id, file.ModifiedTime))
            {
                // Also check that the local file still exists
                if (File.Exists(localPath))
                {
                    skipped++;
                    continue;
                }
            }

            progress?.Report($"Downloading {i + 1}/{remoteFiles.Count}: {file.Name}");
            await DownloadFileAsync(file.Id, localPath);

            if (file.ModifiedTime.HasValue)
                metadata.UpdateEntry(file.Id, file.Name, file.ModifiedTime.Value);

            downloaded++;
        }

        await metadata.SaveAsync();

        progress?.Report(downloaded == 0
            ? $"All {remoteFiles.Count} files up to date."
            : $"Synced {downloaded} file(s), {skipped} already up to date.");

        return remoteFiles;
    }

    /// <summary>
    /// Revokes the OAuth token and clears cached credentials.
    /// </summary>
    public async Task LogoutAsync()
    {
        if (_credential != null)
        {
            await _credential.RevokeTokenAsync(CancellationToken.None);
            _credential = null;
        }

        _driveService?.Dispose();
        _driveService = null;

        // Delete cached tokens
        var tokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TokenFolder);
        if (Directory.Exists(tokenPath))
            Directory.Delete(tokenPath, true);
    }
}
