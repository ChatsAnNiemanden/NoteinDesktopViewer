using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NoteinDesktopViewer;

public partial class FolderInputDialog : Window
{
    public string FolderName { get; private set; } = "NoteInDataSync";

    public FolderInputDialog()
    {
        InitializeComponent();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        FolderName = FolderNameInput.Text ?? "NoteInDataSync";
        Close(FolderName);
    }
}
