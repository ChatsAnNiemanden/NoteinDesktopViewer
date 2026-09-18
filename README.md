# NoteinDesktopViewer

> [!WARNING]
> **Important Notices:**
> 1. This application is mostly **vibe coded**.
> 2. This application is **NOT affiliated with, endorsed by, or connected to Orion/Notein in any way**.

## About

NoteinDesktopViewer is a basic desktop viewer for Notein notes. 

**Core philosophy:** This application will **always remain a basic viewer**. It will never receive features to edit notes or perform other modifications.

## Compatibility & Reverse Engineering

The backup file format was reverse-engineered to create this viewer, and nothing else. Because of this:
- **Only basic stuff is working.**
- **The application could break with any future update of Notein.**

It currently supports the features of Notein **v1.3.210.0** that I personally use, but I make no guarantees about what other features work or do not work. Use at your own risk.

## Setup & Building

This project is built using .NET and Avalonia UI. To use this application, you must build it yourself and provide your own Google Drive API credentials.

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) (check the `.csproj` for the exact version)
- An IDE like [Visual Studio](https://visualstudio.microsoft.com/), [Rider](https://www.jetbrains.com/rider/), or [VS Code](https://code.visualstudio.com/) (with Avalonia extensions recommended).

### Google Drive API Setup

1. Create a Google Cloud account.
2. Enable the Google Drive API for your project.
3. Create OAuth 2.0 Client IDs credentials (Desktop app).
4. Download the JSON credentials and save them as `client_secrets.json` in the project root directory.
5. Add yourself as a test user in the OAuth consent screen.

### Build and Run

You can build and run the project using the .NET CLI or your preferred IDE.

**Using .NET CLI:**

Open a terminal in the project root directory and run:

```bash
# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Run the application
dotnet run
```


<img width="2404" height="1668" alt="NoteinDesktopViewerScreenshot" src="https://github.com/user-attachments/assets/93579f05-87b6-4e45-93fe-87ffe5e4d30a" />
