using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NoteinDesktopViewer.Conversion;

/// <summary>
/// Shared conversion logic for converting Notein note files to PDFs.
/// Eliminates the duplication between GoogleDriveService and LocalFolderService.
/// </summary>
public static class NoteConversionService
{
    /// <summary>
    /// Converts note files from the source folder to PDFs in the output folder.
    /// Skips files that already have a corresponding up-to-date PDF.
    /// </summary>
    /// <param name="noteFiles">The list of note files to convert.</param>
    /// <param name="pdfFolder">The output folder for PDFs.</param>
    /// <param name="onFileConverted">Optional callback invoked after a file is successfully converted.</param>
    public static async Task ConvertAllToPdfAsync(
        IReadOnlyList<FileInfo> noteFiles, 
        string pdfFolder, 
        IProgress<string>? progress = null,
        Func<FileInfo, Task>? onFileConverted = null)
    {
        Directory.CreateDirectory(pdfFolder);

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
            var pdfPath = Path.Combine(pdfFolder, pdfName);

            // Skip if PDF already exists and is newer than the source file
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
                if (onFileConverted != null)
                {
                    await onFileConverted(noteFile);
                }
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
