using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Linq;
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
        StatusText.Text = "Signing in...";

        try
        {
            var email = await _driveService.LoginAsync();
            StatusText.Text = email;
            LoginButton.IsVisible = false;
            LogoutButton.IsVisible = true;

            await LoadFilesAsync();
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
        StatusText.Text = "Not signed in";
        LogoutButton.IsVisible = false;
        LogoutButton.IsEnabled = true;
        LoginButton.IsVisible = true;
        LoginButton.IsEnabled = true;
    }

    private async Task LoadFilesAsync()
    {
        LoadingPanel.IsVisible = true;
        ErrorText.IsVisible = false;

        try
        {
            var files = await _driveService.ListNoteInDataSyncFilesAsync();

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
            ErrorText.Text = $"Failed to load files: {ex.Message}";
            ErrorText.IsVisible = true;
        }
        finally
        {
            LoadingPanel.IsVisible = false;
        }
    }
}