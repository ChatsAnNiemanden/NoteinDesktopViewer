using Avalonia.Controls;
using System.Text;

namespace NoteinDesktopViewer;

public partial class LicensesWindow : Window
{
    public LicensesWindow()
    {
        InitializeComponent();
        
        var sb = new StringBuilder();
        sb.AppendLine("This application uses the following open-source software:");
        sb.AppendLine("---------------------------------------------------------");
        sb.AppendLine();
        
        sb.AppendLine("Avalonia UI (MIT License)");
        sb.AppendLine("Copyright (c) The Avalonia Project");
        sb.AppendLine("https://github.com/AvaloniaUI/Avalonia");
        sb.AppendLine();
        
        sb.AppendLine("Avalonia.Controls.WebView (MIT License)");
        sb.AppendLine("https://github.com/mott/Avalonia.WebView");
        sb.AppendLine();
        
        sb.AppendLine("Google.Apis.Drive.v3 (Apache-2.0 License)");
        sb.AppendLine("Copyright 2021 Google LLC");
        sb.AppendLine();
        
        sb.AppendLine("Microsoft.Data.Sqlite (MIT License)");
        sb.AppendLine("Copyright (c) Microsoft Corporation");
        sb.AppendLine();
        
        sb.AppendLine("PdfPig (Apache-2.0 License)");
        sb.AppendLine("Copyright (c) 2018 BobLd");
        sb.AppendLine();
        
        sb.AppendLine("PDFsharp (MIT License)");
        sb.AppendLine("Copyright (c) 2005-2024 empira Software GmbH, Troisdorf (Germany)");
        sb.AppendLine();
        
        sb.AppendLine("SkiaSharp (MIT License)");
        sb.AppendLine("Copyright (c) 2015-2016 Xamarin, Inc.");
        sb.AppendLine("Copyright (c) 2017-2018 Microsoft Corporation");
        sb.AppendLine();
        
        sb.AppendLine("PDF.js (Apache-2.0 License)");
        sb.AppendLine("Copyright 2012 Mozilla Foundation");
        
        var licensesText = this.FindControl<TextBlock>("LicensesText");
        if (licensesText != null)
        {
            licensesText.Text = sb.ToString();
        }
    }
}
