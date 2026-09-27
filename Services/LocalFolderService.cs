using NoteinDesktopViewer.Conversion;
using NoteinDesktopViewer.Helpers;
using NoteinDesktopViewer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NoteinDesktopViewer.Services;

public class LocalFolderService : INoteSourceService
{
    public string SourceFolder { get; set; } = string.Empty;

    public string LocalPdfFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoteinDesktopViewer", "PDFs");

    public async Task<List<DriveFileInfo>> SyncFilesAsync(IProgress<string>? progress = null)
    {
        return await Task.Run(async () =>
        {
            progress?.Report("Scanning local folder...");
            var files = new List<DriveFileInfo>();
            
            if (!Directory.Exists(SourceFolder))
            {
                progress?.Report("Local folder does not exist or is not set.");
                return files;
            }

            var metadata = await SyncMetadata.LoadAsync();
            Directory.CreateDirectory(LocalPdfFolder);

            var noteFilePaths = Directory.GetFiles(SourceFolder, "*.*")
                                         .Where(f => !f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && 
                                                     !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                                         .ToList();

            int changed = 0;
            int upToDate = 0;

            foreach (var f in noteFilePaths)
            {
                var fi = new FileInfo(f);
                files.Add(new DriveFileInfo 
                { 
                    Id = f, 
                    Name = fi.Name, 
                    ModifiedTime = fi.LastWriteTimeUtc 
                });

                var pdfName = Path.GetFileNameWithoutExtension(fi.Name) + ".pdf";
                var pdfPath = Path.Combine(LocalPdfFolder, pdfName);

                bool isUpToDate = !metadata.HasChanged(f, fi.LastWriteTimeUtc);

                // If metadata hasn't tracked it yet but an up-to-date PDF already exists on disk, backfill metadata
                if (!isUpToDate && File.Exists(pdfPath) && File.GetLastWriteTimeUtc(pdfPath) >= fi.LastWriteTimeUtc)
                {
                    await metadata.UpdateEntryAsync(f, fi.Name, fi.LastWriteTimeUtc, "LocalFolder");
                    isUpToDate = true;
                }

                if (isUpToDate && File.Exists(pdfPath))
                {
                    upToDate++;
                }
                else
                {
                    changed++;
                }
            }

            if (files.Count == 0)
            {
                progress?.Report("No note files found in local folder.");
            }
            else if (changed == 0)
            {
                progress?.Report($"All {files.Count} files up to date.");
            }
            else
            {
                progress?.Report($"Found {files.Count} files ({changed} changed/new, {upToDate} up to date).");
            }

            return files;
        });
    }

    public async Task ConvertAllToPdfAsync(IProgress<string>? progress = null, Action<string>? onFileConverted = null)
    {
        await Task.Run(async () =>
        {
            if (!Directory.Exists(SourceFolder))
            {
                progress?.Report("Local folder does not exist.");
                return;
            }

            var metadata = await SyncMetadata.LoadAsync();

            var noteFiles = Directory.GetFiles(SourceFolder, "*.*")
                .Where(f => !f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && 
                            !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .ToList();

            await NoteConversionService.ConvertAllToPdfAsync(
                noteFiles, 
                LocalPdfFolder, 
                progress,
                async noteFile =>
                {
                    await metadata.UpdateEntryAsync(
                        noteFile.FullName, 
                        noteFile.Name, 
                        noteFile.LastWriteTimeUtc, 
                        "LocalFolder");
                    onFileConverted?.Invoke(noteFile.Name);
                });
        });
    }
}
