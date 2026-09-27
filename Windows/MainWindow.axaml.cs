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
using NoteinDesktopViewer.Services;
using NoteinDesktopViewer.Helpers;
using System.Threading;

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
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private bool _isSigningIn = false;
    private bool _initializing = false;

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

        _initializing = true;
        if (_settings.LastSourceIndex >= 0 && _settings.LastSourceIndex < SourceComboBox.Items.Count)
        {
            SourceComboBox.SelectedIndex = _settings.LastSourceIndex;
        }
        else
        {
            SourceComboBox.SelectedIndex = 0;
        }
        _initializing = false;

        UpdateSourceUI();
    }

    private void OnSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

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
        if (_isSigningIn || !_driveService.HasSavedToken)
            return;

        _isSigningIn = true;
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
            _isSigningIn = false;
            LoadingPanel.IsVisible = false;
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
        if (!await _syncLock.WaitAsync(0))
        {
            // Another sync is already running
            return;
        }

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
            _syncLock.Release();
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
        using var stream = typeof(MainWindow).Assembly
            .GetManifestResourceStream("NoteinDesktopViewer.Assets.PdfViewer.html")
            ?? throw new InvalidOperationException("Embedded PdfViewer.html not found");
        using var reader = new StreamReader(stream);
        var template = reader.ReadToEnd();
        return template.Replace("{{base64Pdf}}", base64Pdf);
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