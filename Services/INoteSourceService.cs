using NoteinDesktopViewer.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NoteinDesktopViewer.Services;

public interface INoteSourceService
{
    string LocalPdfFolder { get; }
    Task<List<DriveFileInfo>> SyncFilesAsync(IProgress<string>? progress = null);
    Task ConvertAllToPdfAsync(IProgress<string>? progress = null, Action<string>? onFileConverted = null, Action<string>? onFileFailed = null);
}
