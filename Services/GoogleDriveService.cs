#if !DISABLE_GOOGLE_DRIVE
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using NoteinDesktopViewer.Conversion;
using NoteinDesktopViewer.Helpers;
using NoteinDesktopViewer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NoteinDesktopViewer.Services;

public class GoogleDriveService : INoteSourceService
{
    private static readonly string[] Scopes = { DriveService.Scope.DriveReadonly };
    private const string ApplicationName = "NoteinDesktopViewer";
    private const string TokenFolder = "NoteinDesktopViewer_Tokens";
    public string TargetFolderName { get; set; } = "NoteInDataSync";

    public string LocalSyncFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteinDesktopViewer", TargetFolderName);

    public string LocalPdfFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteinDesktopViewer", "PDFs");

    private UserCredential? _credential;
    private DriveService? _driveService;
    private bool _isOffline;

    public bool IsOffline => _isOffline;

    public bool IsLoggedIn => _credential != null || (HasSavedToken && _isOffline);

    public bool HasSavedToken
    {
        get
        {
            var tokenPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TokenFolder);
            return Directory.Exists(tokenPath) && Directory.EnumerateFiles(tokenPath).Any();
        }
    }

    public static bool IsNetworkOrOfflineException(Exception? ex)
    {
        if (ex == null) return false;

        if (ex is AggregateException agg)
        {
            return agg.InnerExceptions.Any(IsNetworkOrOfflineException);
        }

        if (ex is System.Net.Http.HttpRequestException ||
            ex is System.Net.Sockets.SocketException ||
            ex is System.Net.WebException ||
            ex is TimeoutException ||
            ex is TaskCanceledException ||
            ex is Google.GoogleApiException ||
            ex is Google.Apis.Auth.OAuth2.Responses.TokenResponseException)
        {
            return true;
        }

        if (ex is IOException ioEx &&
            (ioEx.Message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
             ioEx.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
             ioEx.Message.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
             ioEx.Message.Contains("aborted", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return ex.InnerException != null && IsNetworkOrOfflineException(ex.InnerException);
    }

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

        try
        {
            _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                (await GoogleClientSecrets.FromStreamAsync(stream)).Secrets,
                Scopes,
                "user",
                CancellationToken.None,
                new FileDataStore(tokenPath, true));
        }
        catch (Exception ex) when (HasSavedToken && IsNetworkOrOfflineException(ex))
        {
            _isOffline = true;
            var settings = AppSettings.Load();
            return !string.IsNullOrEmpty(settings.LastGoogleDriveEmail)
                ? $"{settings.LastGoogleDriveEmail} (Offline)"
                : "Google Drive (Offline)";
        }

        _driveService = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = _credential,
            ApplicationName = ApplicationName
        });

        // Fetch user email from the about endpoint
        try
        {
            var aboutRequest = _driveService.About.Get();
            aboutRequest.Fields = "user";
            var about = await aboutRequest.ExecuteAsync();
            var email = about.User.EmailAddress ?? "Unknown";
            _isOffline = false;
            var settings = AppSettings.Load();
            settings.LastGoogleDriveEmail = email;
            settings.Save();
            return email;
        }
        catch (Exception ex) when (IsNetworkOrOfflineException(ex))
        {
            _isOffline = true;
            var settings = AppSettings.Load();
            return !string.IsNullOrEmpty(settings.LastGoogleDriveEmail)
                ? $"{settings.LastGoogleDriveEmail} (Offline)"
                : "Google Drive (Offline)";
        }
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
            listRequest.Q = $"'{folder.Id}' in parents and trashed = false and mimeType != 'application/vnd.google-apps.folder'";
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

        var tempPath = destPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var request = _driveService.Files.Get(fileId);
                await request.DownloadAsync(fileStream);
            }

            File.Move(tempPath, destPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Returns files from the local sync folder.
    /// </summary>
    public List<DriveFileInfo> GetLocalSyncedFiles()
    {
        var result = new List<DriveFileInfo>();
        if (!Directory.Exists(LocalSyncFolder))
            return result;

        var dir = new DirectoryInfo(LocalSyncFolder);
        foreach (var fi in dir.GetFiles())
        {
            if (fi.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new DriveFileInfo
            {
                Id = fi.FullName,
                Name = fi.Name,
                ModifiedTime = fi.LastWriteTimeUtc
            });
        }
        return result;
    }

    /// <summary>
    /// Syncs the NoteInDataSync folder: downloads new/modified files, skips unchanged ones.
    /// Reports progress via the callback (e.g. "Downloading 3/12: notes.db").
    /// Returns the list of all remote files (or local cached files when offline).
    /// </summary>
    public async Task<List<DriveFileInfo>> SyncFilesAsync(IProgress<string>? progress = null)
    {
        if (_driveService == null && HasSavedToken)
        {
            try
            {
                await LoginAsync();
            }
            catch { }
        }

        if (_driveService == null)
        {
            _isOffline = true;
            progress?.Report("Offline: skipped syncing with Google Drive.");
            return GetLocalSyncedFiles();
        }

        progress?.Report("Checking for changes...");

        List<DriveFileInfo> remoteFiles;
        try
        {
            remoteFiles = await ListNoteInDataSyncFilesAsync();
            _isOffline = false;
        }
        catch (Exception ex) when (IsNetworkOrOfflineException(ex))
        {
            _isOffline = true;
            progress?.Report("Offline: skipped syncing with Google Drive.");
            return GetLocalSyncedFiles();
        }

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

            try
            {
                progress?.Report($"Downloading {i + 1}/{remoteFiles.Count}: {file.Name}");
                await DownloadFileAsync(file.Id, localPath);

                if (file.ModifiedTime.HasValue)
                    await metadata.UpdateEntryAsync(file.Id, file.Name, file.ModifiedTime.Value, "GoogleDrive");

                downloaded++;
            }
            catch (Exception ex) when (IsNetworkOrOfflineException(ex))
            {
                _isOffline = true;
                progress?.Report($"Connection lost. Skipping remaining downloads.");
                break;
            }
        }

        await metadata.SaveAsync();

        if (_isOffline)
        {
            progress?.Report($"Offline: downloaded {downloaded} file(s) before connection was lost.");
            return GetLocalSyncedFiles();
        }

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
        _isOffline = false;
        if (_credential != null)
        {
            try
            {
                await _credential.RevokeTokenAsync(CancellationToken.None);
            }
            catch { }
            _credential = null;
        }

        _driveService?.Dispose();
        _driveService = null;

        // Delete cached tokens
        var tokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TokenFolder);
        if (Directory.Exists(tokenPath))
            Directory.Delete(tokenPath, true);

        var settings = AppSettings.Load();
        settings.LastGoogleDriveEmail = string.Empty;
        settings.Save();
    }

    /// <summary>
    /// Converts all downloaded note files in the sync folder to PDFs.
    /// Skips files that already have a corresponding up-to-date PDF.
    /// </summary>
    public async Task ConvertAllToPdfAsync(
        IProgress<string>? progress = null, 
        Action<string>? onFileConverted = null, 
        Action<string>? onFileFailed = null)
    {
        await Task.Run(async () =>
        {
            if (!Directory.Exists(LocalSyncFolder))
            {
                progress?.Report("No local files to convert.");
                return;
            }

            var noteFiles = Directory.GetFiles(LocalSyncFolder)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .ToList();

            await NoteConversionService.ConvertAllToPdfAsync(
                noteFiles, 
                LocalPdfFolder, 
                progress,
                async noteFile =>
                {
                    onFileConverted?.Invoke(noteFile.Name);
                },
                async (noteFile, ex) =>
                {
                    onFileFailed?.Invoke(noteFile.Name);
                });
        });
    }
}
#endif
