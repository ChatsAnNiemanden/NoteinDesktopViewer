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
    private PdfViewerServer? _pdfServer;

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
            var pdfBytes = await File.ReadAllBytesAsync(pdfPath);
            var base64 = Convert.ToBase64String(pdfBytes);
            var html = BuildPdfViewerHtml(base64);

            _pdfServer ??= new PdfViewerServer();
            _pdfServer.OnReexportRequested -= TriggerReexport; // Prevent duplicate handlers
            _pdfServer.OnReexportRequested += TriggerReexport;
            _pdfServer.SetContent(html);

            PdfPlaceholder.IsVisible = false;
            PdfWebView.IsVisible = true;
            PdfWebView.Navigate(new Uri($"http://127.0.0.1:{_pdfServer.Port}/"));
        }
        catch (Exception ex)
        {
            PdfPlaceholder.Text = $"Error loading PDF: {ex.Message}";
            PdfPlaceholder.IsVisible = true;
            PdfWebView.IsVisible = false;
        }
    }

    private string BuildPdfViewerHtml(string base64Pdf)
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
                #controls { position: fixed; top: 10px; right: 20px; background: rgba(40,40,40,0.8); color: white; padding: 8px 12px; border-radius: 6px; font-family: sans-serif; z-index: 1000; box-shadow: 0 2px 10px rgba(0,0,0,0.5); display: flex; align-items: center; gap: 8px; }
                button { background: #337ab7; color: white; border: none; padding: 4px 8px; border-radius: 4px; cursor: pointer; }
                button:hover { background: #286090; }
            </style>
        </head>
        <body>
            <svg style="display:none;">
                <filter id="stroke-darken">
                    <feComponentTransfer>
                        <feFuncR type="gamma" id="gammaR" amplitude="1" exponent="1" offset="0"/>
                        <feFuncG type="gamma" id="gammaG" amplitude="1" exponent="1" offset="0"/>
                        <feFuncB type="gamma" id="gammaB" amplitude="1" exponent="1" offset="0"/>
                    </feComponentTransfer>
                </filter>
            </svg>
            <div id="controls">
                <label for="darken">Darken Strokes:</label>
                <input type="range" id="darken" min="1" max="5" step="0.2" value="1">
                <span id="darken-val">Off</span>
                <button id="reexport-btn">Re-export PDF</button>
                <button id="reset-btn" style="background: #d9534f;">Reset</button>
            </div>
            <div id="viewer" style="filter: none"></div>
            <script src="https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.10.38/pdf.min.mjs" type="module"></script>
            <script type="module">
                import * as pdfjsLib from 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.10.38/pdf.min.mjs';
                pdfjsLib.GlobalWorkerOptions.workerSrc = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.10.38/pdf.worker.min.mjs';

                const viewer = document.getElementById('viewer');
                const darkenInput = document.getElementById('darken');
                const darkenVal = document.getElementById('darken-val');
                const gammaR = document.getElementById('gammaR');
                const gammaG = document.getElementById('gammaG');
                const gammaB = document.getElementById('gammaB');
                const reexportBtn = document.getElementById('reexport-btn');
                const resetBtn = document.getElementById('reset-btn');
                
                function updateFilter() {
                    const e = darkenInput.value;
                    darkenVal.textContent = e == 1 ? 'Off' : e;
                    gammaR.setAttribute('exponent', e);
                    gammaG.setAttribute('exponent', e);
                    gammaB.setAttribute('exponent', e);
                    viewer.style.filter = e == 1 ? 'none' : 'url(#stroke-darken)';
                }
                
                darkenInput.addEventListener('input', updateFilter);

                async function renderPdf(base64Data) {
                    const pdfUrl = 'data:application/pdf;base64,' + base64Data;
                    const pdf = await pdfjsLib.getDocument(pdfUrl).promise;
                    
                    // Maintain height to prevent scroll jumping while rendering
                    const oldScroll = window.scrollY;
                    viewer.style.minHeight = viewer.clientHeight + 'px';
                    viewer.innerHTML = '';
                    
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
                    viewer.style.minHeight = '0px';
                    window.scrollTo(0, oldScroll);
                }

                async function applyExport(darkenValue) {
                    reexportBtn.disabled = true;
                    resetBtn.disabled = true;
                    try {
                        const res = await fetch('/reexport?darken=' + darkenValue);
                        const newBase64 = await res.text();
                        if (newBase64 && newBase64.length > 0 && !newBase64.startsWith("HTTP")) {
                            darkenInput.value = 1;
                            updateFilter();
                            await renderPdf(newBase64);
                        }
                    } finally {
                        reexportBtn.disabled = false;
                        resetBtn.disabled = false;
                    }
                }
                
                reexportBtn.addEventListener('click', () => applyExport(darkenInput.value));
                resetBtn.addEventListener('click', () => applyExport(1));

                // Initial load
                renderPdf('{{base64Pdf}}');
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

    private async Task<string> TriggerReexport(double factor)
    {
        FileItem? selectedItem = null;
        await Dispatcher.UIThread.InvokeAsync(() => 
        { 
            selectedItem = FileListBox.SelectedItem as FileItem;
        });

        if (selectedItem == null || selectedItem.FileName == "(No files found)")
            return "";

        if (_settings != null)
        {
            _settings.StrokeDarkenFactor = factor;
            _settings.Save();
        }

        await Dispatcher.UIThread.InvokeAsync(() => 
        {
            LoadingPanel.IsVisible = true;
            LoadingText.Text = "Re-exporting PDF...";
        });

        string fileName = selectedItem.FileName;
        try
        {
            var pdfName = Path.GetFileNameWithoutExtension(fileName) + ".pdf";
            var pdfPath = Path.Combine(_activeService.LocalPdfFolder, pdfName);
            
            if (File.Exists(pdfPath))
            {
                File.Delete(pdfPath);
            }
            
            // Regenerate PDF silently
            await _activeService.ConvertAllToPdfAsync();

            if (File.Exists(pdfPath))
            {
                var pdfBytes = await File.ReadAllBytesAsync(pdfPath);
                var base64 = Convert.ToBase64String(pdfBytes);
                
                await Dispatcher.UIThread.InvokeAsync(() => 
                {
                    var html = BuildPdfViewerHtml(base64);
                    if (_pdfServer != null)
                        _pdfServer.SetContent(html);
                });
                return base64;
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => 
            {
                ErrorText.Text = $"Failed to re-export PDF: {ex.Message}";
                ErrorText.IsVisible = true;
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => 
            {
                LoadingPanel.IsVisible = false;
            });
        }
        return "";
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _pdfServer?.Dispose();
    }
}