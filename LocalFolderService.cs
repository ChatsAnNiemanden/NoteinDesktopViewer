using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NoteinDesktopViewer;

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
        Directory.CreateDirectory(LocalPdfFolder);

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

        if (noteFiles.Count == 0)
        {
            progress?.Report("No files to convert.");
            return;
        }

        int converted = 0;
        int skipped = 0;

        for (int i = 0; i < noteFiles.Count; i++)
        {
            var noteFile = noteFiles[i];
            var pdfName = Path.GetFileNameWithoutExtension(noteFile.Name) + ".pdf";
            var pdfPath = Path.Combine(LocalPdfFolder, pdfName);

            if (File.Exists(pdfPath) && File.GetLastWriteTimeUtc(pdfPath) >= noteFile.LastWriteTimeUtc)
            {
                skipped++;
                continue;
            }

            progress?.Report($"Converting {i + 1}/{noteFiles.Count}: {noteFile.Name}");

            try
            {
                await Task.Run(() => NoteinToPdf.ConvertSingleNote(noteFile, pdfPath));
                converted++;
            }
            catch (Exception ex)
            {
                progress?.Report($"Failed to convert {noteFile.Name}: {ex.Message}");
            }
        }

        progress?.Report(converted == 0
            ? $"All {noteFiles.Count} PDFs up to date."
            : $"Converted {converted} file(s) to PDF, {skipped} already up to date.");
    }
}
