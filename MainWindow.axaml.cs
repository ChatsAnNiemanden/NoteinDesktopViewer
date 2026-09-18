using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace NoteinDesktopViewer;

public class FileItem
{
    public string FileName { get; set; } = string.Empty;
    public Bitmap? PreviewImage { get; set; }
    public bool HasPdf { get; set; }
}

public partial class MainWindow : Window
{
    private readonly GoogleDriveService _driveService = new();

    public MainWindow()
    {
        InitializeComponent();
        SyncOnStartupWhenLoggedIn();
    }

    private async void SyncOnStartupWhenLoggedIn()
    {
        if (_driveService.HasSavedToken)
        {
            LoginButton.IsVisible = false;
            LogoutButton.IsVisible = true;
            LogoutButton.IsEnabled = false; // Disable until loaded
            LoadingPanel.IsVisible = true;
            LoadingText.Text = "Signing in...";
            StatusText.Text = "Signing in...";

            try
            {
                var email = await _driveService.LoginAsync();
                StatusText.Text = email;
                LogoutButton.IsEnabled = true;
                await SyncAndDisplayFilesAsync();
            }
            catch (Exception ex)
            {
                ErrorText.Text = $"Login failed: {ex.Message}";
                ErrorText.IsVisible = true;
                StatusText.Text = "Not signed in";
                LoginButton.IsVisible = true;
                LogoutButton.IsVisible = false;
            }
            finally
            {
                LoadingPanel.IsVisible = false;
            }
        }
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        ErrorText.IsVisible = false;
        LoadingPanel.IsVisible = true;
        LoadingText.Text = "Signing in...";
        StatusText.Text = "Signing in...";

        try
        {
            var email = await _driveService.LoginAsync();
            StatusText.Text = email;
            LoginButton.IsVisible = false;
            LogoutButton.IsVisible = true;

            await SyncAndDisplayFilesAsync();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Login failed: {ex.Message}";
            ErrorText.IsVisible = true;
            StatusText.Text = "Not signed in";
            LoginButton.IsEnabled = true;
        }
        finally
        {
            LoadingPanel.IsVisible = false;
        }
    }

    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        LogoutButton.IsEnabled = false;
        ErrorText.IsVisible = false;

        try
        {
            await _driveService.LogoutAsync();
        }
        catch
        {
            // Ignore errors during logout
        }

        FileListBox.ItemsSource = null;
        LogBox.Text = "";
        LogBox.IsVisible = false;
        PdfWebView.IsVisible = false;
        PdfPlaceholder.IsVisible = true;
        StatusText.Text = "Not signed in";
        LogoutButton.IsVisible = false;
        LogoutButton.IsEnabled = true;
        LoginButton.IsVisible = true;
        LoginButton.IsEnabled = true;
    }

    private async Task SyncAndDisplayFilesAsync()
    {
        LoadingPanel.IsVisible = true;
        ErrorText.IsVisible = false;
        LogBox.Text = "";
        LogBox.IsVisible = true;

        try
        {
            // Progress reporter updates the loading text on the UI thread
            var progress = new Progress<string>(msg =>
                Dispatcher.UIThread.Post(() => LoadingText.Text = msg));

            var files = await _driveService.SyncFilesAsync(progress);

            // Convert downloaded notes to PDFs, capturing Console output to the log box
            await ConvertWithLogCaptureAsync(progress);

            if (files.Count == 0)
            {
                FileListBox.ItemsSource = new[] { new FileItem { FileName = "(No files found in NoteInDataSync folder)", HasPdf = true } };
            }
            else
            {
                var items = new List<FileItem>();
                foreach (var f in files)
                {
                    var pdfName = Path.GetFileNameWithoutExtension(f.Name) + ".pdf";
                    var pngName = Path.GetFileNameWithoutExtension(f.Name) + ".png";
                    var pdfPath = Path.Combine(GoogleDriveService.LocalPdfFolder, pdfName);
                    var pngPath = Path.Combine(GoogleDriveService.LocalPdfFolder, pngName);
                    
                    bool hasPdf = File.Exists(pdfPath);
                    
                    Bitmap? bmp = null;
                    if (File.Exists(pngPath))
                    {
                        try { bmp = new Bitmap(pngPath); }
                        catch { }
                    }
                    items.Add(new FileItem { FileName = f.Name, PreviewImage = bmp, HasPdf = hasPdf });
                }
                FileListBox.ItemsSource = items;
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Sync failed: {ex.Message}";
            ErrorText.IsVisible = true;
        }
        finally
        {
            LoadingPanel.IsVisible = false;
        }
    }

    private async void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (FileListBox.SelectedItem is not FileItem selectedItem)
            return;

        var fileName = selectedItem.FileName;

        var pdfName = Path.GetFileNameWithoutExtension(fileName) + ".pdf";
        var pdfPath = Path.Combine(GoogleDriveService.LocalPdfFolder, pdfName);

        if (!File.Exists(pdfPath))
        {
            PdfWebView.IsVisible = false;
            PdfPlaceholder.Text = $"No PDF found for {fileName}";
            PdfPlaceholder.IsVisible = true;
            return;
        }

        try
        {
            // Read PDF as base64 and load into PDF.js viewer
            var pdfBytes = await File.ReadAllBytesAsync(pdfPath);
            var base64 = Convert.ToBase64String(pdfBytes);
            var html = BuildPdfViewerHtml(base64);

            var tempHtmlPath = Path.Combine(Path.GetTempPath(), "notein_viewer.html");
            File.WriteAllText(tempHtmlPath, html);

            PdfPlaceholder.IsVisible = false;
            PdfWebView.IsVisible = true;
            PdfWebView.Navigate(new Uri($"file:///{tempHtmlPath.Replace('\\', '/')}"));
        }
        catch (Exception ex)
        {
            PdfPlaceholder.Text = $"Error loading PDF: {ex.Message}";
            PdfPlaceholder.IsVisible = true;
            PdfWebView.IsVisible = false;
        }
    }

    private static string BuildPdfViewerHtml(string base64Pdf)
    {
        return $$"""
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8">
            <style>
                * { margin: 0; padding: 0; box-sizing: border-box; }
                body { background: #525659; overflow: auto; }
                canvas { display: block; margin: 8px auto; box-shadow: 0 2px 8px rgba(0,0,0,0.3); }
            </style>
        </head>
        <body>
            <div id="viewer"></div>
            <script src="https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.10.38/pdf.min.mjs" type="module"></script>
            <script type="module">
                import * as pdfjsLib from 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.10.38/pdf.min.mjs';
                pdfjsLib.GlobalWorkerOptions.workerSrc = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.10.38/pdf.worker.min.mjs';

                const pdfUrl = 'data:application/pdf;base64,{{base64Pdf}}';
                const pdf = await pdfjsLib.getDocument(pdfUrl).promise;
                const viewer = document.getElementById('viewer');

                for (let i = 1; i <= pdf.numPages; i++) {
                    const page = await pdf.getPage(i);
                    const scale = 1.5;
                    const viewport = page.getViewport({ scale });
                    const canvas = document.createElement('canvas');
                    canvas.width = viewport.width;
                    canvas.height = viewport.height;
                    viewer.appendChild(canvas);
                    await page.render({ canvasContext: canvas.getContext('2d'), viewport }).promise;
                }
            </script>
        </body>
        </html>
        """;
    }

    private async Task ConvertWithLogCaptureAsync(IProgress<string> progress)
    {
        // Redirect Console.Out and Console.Error to capture NoteinToPdf output
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var logWriter = new UITextWriter(line =>
            Dispatcher.UIThread.Post(() => AppendLog(line)));

        Console.SetOut(logWriter);
        Console.SetError(logWriter);
        try
        {
            await GoogleDriveService.ConvertAllToPdfAsync(progress);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        LogBox.Text += text + "\n";
        // Auto-scroll to end
        LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
    }

    /// <summary>
    /// A TextWriter that forwards complete lines to a callback action.
    /// </summary>
    private class UITextWriter : TextWriter
    {
        private readonly Action<string> _onLine;
        private readonly StringBuilder _buffer = new();

        public UITextWriter(Action<string> onLine) => _onLine = onLine;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
                FlushBuffer();
            else if (value != '\r')
                _buffer.Append(value);
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            foreach (var ch in value)
                Write(ch);
        }

        public override void WriteLine(string? value)
        {
            if (value != null) _buffer.Append(value);
            FlushBuffer();
        }

        private void FlushBuffer()
        {
            var line = _buffer.ToString();
            _buffer.Clear();
            _onLine(line);
        }
    }
}