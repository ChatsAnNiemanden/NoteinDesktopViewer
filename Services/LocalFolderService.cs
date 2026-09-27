using NoteinDesktopViewer.Conversion;
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

    public Task<List<DriveFileInfo>> SyncFilesAsync(IProgress<string>? progress = null)
    {
        progress?.Report("Scanning local folder...");
        var files = new List<DriveFileInfo>();
        
        if (Directory.Exists(SourceFolder))
        {
            var noteFiles = Directory.GetFiles(SourceFolder, "*.*")
                                     .Where(f => !f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && 
                                                 !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            
            foreach (var f in noteFiles)
            {
                var fi = new FileInfo(f);
                files.Add(new DriveFileInfo 
                { 
                    Id = f, 
                    Name = fi.Name, 
                    ModifiedTime = fi.LastWriteTimeUtc 
                });
            }

            progress?.Report($"Found {files.Count} files in local folder.");
        }
        else
        {
            progress?.Report("Local folder does not exist or is not set.");
        }

        return Task.FromResult(files);
    }

    public async Task ConvertAllToPdfAsync(IProgress<string>? progress = null)
    {
        if (!Directory.Exists(SourceFolder))
        {
            progress?.Report("Local folder does not exist.");
            return;
        }

        var noteFiles = Directory.GetFiles(SourceFolder, "*.*")
            .Where(f => !f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && 
                        !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .ToList();

        await NoteConversionService.ConvertAllToPdfAsync(noteFiles, LocalPdfFolder, progress);
    }
}
