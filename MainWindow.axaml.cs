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
using System.Text.RegularExpressions;

namespace NoteinDesktopViewer;

public class FileItem
{
    public string FileName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public Bitmap? PreviewImage { get; set; }
    public bool HasPdf { get; set; }
}

public partial class MainWindow : Window
{
#if !DISABLE_GOOGLE_DRIVE
    private readonly GoogleDriveService _driveService = new();
#endif
    private readonly LocalFolderService _localService = new();
    private INoteSourceService _activeService = null!;
    private readonly AppSettings _settings;

    public MainWindow()
    {
        _settings = AppSettings.Load();
        InitializeComponent();
        
#if !DISABLE_GOOGLE_DRIVE
        SourceComboBox.Items.Add(new ComboBoxItem { Content = "Google Drive" });
#endif
        SourceComboBox.Items.Add(new ComboBoxItem { Content = "Local Folder" });

        if (!string.IsNullOrEmpty(_settings.LastLocalFolder))
        {
            _localService.SourceFolder = _settings.LastLocalFolder;
        }
#if !DISABLE_GOOGLE_DRIVE
        if (!string.IsNullOrEmpty(_settings.GoogleDriveTargetFolder))
        {
            _driveService.TargetFolderName = _settings.GoogleDriveTargetFolder;
        }
#endif

        if (_settings.LastSourceIndex >= 0 && _settings.LastSourceIndex < SourceComboBox.Items.Count)
        {
            SourceComboBox.SelectedIndex = _settings.LastSourceIndex;
        }
        else
        {
            SourceComboBox.SelectedIndex = 0;
        }
        
        UpdateSourceUI();
    }

    private void OnSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSourceUI();
        
        if (_settings != null && SourceComboBox != null)
        {
            _settings.LastSourceIndex = SourceComboBox.SelectedIndex;
            _settings.Save();
        }
    }

    private void UpdateSourceUI()
    {
        if (SourceComboBox == null) return;
        
        FileListBox.ItemsSource = null;
        LogBox.Text = "";
        LogBox.IsVisible = false;
        PdfWebView.IsVisible = false;
        PdfPlaceholder.IsVisible = true;
        
        var selectedContent = (SourceComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        
#if !DISABLE_GOOGLE_DRIVE
        if (selectedContent == "Google Drive") // Google Drive
        {
            _activeService = _driveService;
            SelectLocalFolderButton.IsVisible = false;
            
            if (_driveService.IsLoggedIn)
            {
                LoginButton.IsVisible = false;
                LogoutButton.IsVisible = true;
                ManuelSyncButton.IsEnabled = true;
                StatusText.Text = "Signed in to Google Drive";
                var _ = SyncAndDisplayFilesAsync();
            }
            else if (_driveService.HasSavedToken)
            {
                SyncOnStartupWhenLoggedIn();
            }
            else
            {
                LoginButton.IsVisible = true;
                LogoutButton.IsVisible = false;
                ManuelSyncButton.IsEnabled = false;
                StatusText.Text = "Not signed in";
            }
        }
        else // Local Folder
#endif
        {
            _activeService = _localService;
            LoginButton.IsVisible = false;
            LogoutButton.IsVisible = false;
            SelectLocalFolderButton.IsVisible = true;
            
            if (string.IsNullOrEmpty(_localService.SourceFolder))
            {
                ManuelSyncButton.IsEnabled = false;
                StatusText.Text = "No folder selected";
            }
            else
            {
                ManuelSyncButton.IsEnabled = true;
                StatusText.Text = _localService.SourceFolder;
                var _ = SyncAndDisplayFilesAsync();
            }
        }
    }

    private async void OnSelectLocalFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Select Notein Folder",
            AllowMultiple = false
        });

        if (folders != null && folders.Count > 0)
        {
            _localService.SourceFolder = folders[0].Path.LocalPath;
            StatusText.Text = _localService.SourceFolder;
            ManuelSyncButton.IsEnabled = true;
            
            if (_settings != null)
            {
                _settings.LastLocalFolder = _localService.SourceFolder;
                _settings.Save();
            }
            
            await SyncAndDisplayFilesAsync();
        }
    }

    private async void SyncOnStartupWhenLoggedIn()
    {
#if !DISABLE_GOOGLE_DRIVE
        if (_driveService.HasSavedToken)
        {
            LoginButton.IsVisible = false;
            LogoutButton.IsVisible = true;
            ManuelSyncButton.IsEnabled = true;
            LogoutButton.IsEnabled = false; // Disable until loaded
            LoadingPanel.IsVisible = true;
            LoadingText.Text = "Signing in...";
            StatusText.Text = "Signing in...";

            try
            {
                var email = await _driveService.LoginAsync();
                StatusText.Text = email;
                LogoutButton.IsEnabled = true;

                if (_activeService == _driveService)
                {
                    await SyncAndDisplayFilesAsync();
                }
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
#endif
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
#if !DISABLE_GOOGLE_DRIVE
        LoginButton.IsEnabled = false;
        ManuelSyncButton.IsEnabled = true;
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

            var dialog = new FolderInputDialog();
            var result = await dialog.ShowDialog<string>(this);
            if (!string.IsNullOrWhiteSpace(result))
            {
                _driveService.TargetFolderName = result;
                _settings.GoogleDriveTargetFolder = result;
                _settings.Save();
            }

            if (_activeService == _driveService)
            {
                await SyncAndDisplayFilesAsync();
            }
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
#endif
    }

    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
#if !DISABLE_GOOGLE_DRIVE
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
        ManuelSyncButton.IsEnabled = false;
        LogoutButton.IsEnabled = true;
        LoginButton.IsVisible = true;
        LoginButton.IsEnabled = true;
#endif
    }

    private async void OnManuelSyncClick(object? sender, RoutedEventArgs e)
    {
        await SyncAndDisplayFilesAsync();
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

            var files = await _activeService.SyncFilesAsync(progress);

            // Convert downloaded notes to PDFs, capturing Console output to the log box
            await ConvertWithLogCaptureAsync(progress);

            if (files.Count == 0)
            {
                FileListBox.ItemsSource = new[] { new FileItem { 
                    FileName = "(No files found)", 
                    DisplayName = "(No files found in source)",
                    HasPdf = true 
                } };
            }
            else
            {
                var items = new List<FileItem>();
                foreach (var f in files)
                {
                    var pdfName = Path.GetFileNameWithoutExtension(f.Name) + ".pdf";
                    var pngName = Path.GetFileNameWithoutExtension(f.Name) + ".png";
                    var pdfPath = Path.Combine(_activeService.LocalPdfFolder, pdfName);
                    var pngPath = Path.Combine(_activeService.LocalPdfFolder, pngName);
                    
                    bool hasPdf = File.Exists(pdfPath);
                    
                    Bitmap? bmp = null;
                    if (File.Exists(pngPath))
                    {
                        try { bmp = new Bitmap(pngPath); }
                        catch { }
                    }
                    items.Add(new FileItem { 
                        FileName = f.Name, 
                        DisplayName = FormatDisplayName(f.Name),
                        PreviewImage = bmp, 
                        HasPdf = hasPdf 
                    });
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

    private static string FormatDisplayName(string name)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(name);
        var regex = new Regex(@"(?i)[_-]?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}");
        return regex.Replace(nameWithoutExt, "");
    }

    private async void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (FileListBox.SelectedItem is not FileItem selectedItem)
            return;

        if (selectedItem.FileName == "(No files found)") return;

        var fileName = selectedItem.FileName;

        var pdfName = Path.GetFileNameWithoutExtension(fileName) + ".pdf";
        var pdfPath = Path.Combine(_activeService.LocalPdfFolder, pdfName);

        if (!File.Exists(pdfPath))
        {
            PdfWebView.IsVisible = false;
            PdfPlaceholder.Text = $"No PDF found for {selectedItem.DisplayName}";
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
            await _activeService.ConvertAllToPdfAsync(progress);
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

    private void OnThemeToggleClick(object? sender, RoutedEventArgs e)
    {
        var app = Avalonia.Application.Current;
        if (app is not null)
        {
            if (app.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Light)
            {
                app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            }
            else
            {
                app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            }
        }
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var licensesWindow = new LicensesWindow();
        licensesWindow.ShowDialog(this);
    }
}