using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NoteinDesktopViewer;

public partial class MainWindow : Window
{
    private readonly GoogleDriveService _driveService = new();

    public MainWindow()
    {
        InitializeComponent();
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
                FileListBox.ItemsSource = new[] { "(No files found in NoteInDataSync folder)" };
            }
            else
            {
                FileListBox.ItemsSource = files.Select(f => f.Name).ToList();
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