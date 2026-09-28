using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NoteinDesktopViewer.Helpers;

/// <summary>
/// Minimal in-process HTTP server that serves a single HTML page to the WebView.
/// Uses raw TcpListener instead of HttpListener to avoid sandbox restrictions
/// (e.g. Flatpak) where HttpListener can fail even with --share=network.
/// Using localhost instead of file:// avoids WebKitGTK's restriction that blocks
/// loading external (CDN) scripts from file:// origins on Linux.
/// </summary>
internal sealed class PdfViewerServer : IDisposable
{
    private readonly TcpListener _listener;
    private string _currentHtml = "<html><body>No content</body></html>";
    private bool _disposed;

    public int Port { get; }

    public PdfViewerServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Task.Run(ServeLoopAsync);
    }

    /// <summary>Updates the HTML that will be served on the next request.</summary>
    public void SetContent(string html)
    {
        Volatile.Write(ref _currentHtml, html);
    }

    private async Task ServeLoopAsync()
    {
        while (!_disposed)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            // Handle each connection on a thread-pool thread so the loop stays responsive
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    public event Func<double, Task<string>>? OnReexportRequested;

    private static readonly Lazy<byte[]?> s_pdfJsBytes = new(() => LoadEmbeddedResource("NoteinDesktopViewer.Assets.pdf.mjs"));
    private static readonly Lazy<byte[]?> s_pdfWorkerJsBytes = new(() => LoadEmbeddedResource("NoteinDesktopViewer.Assets.pdf.worker.mjs"));

    private static byte[]? LoadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(PdfViewerServer).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task ServeBytesAsync(Stream stream, byte[]? bytes, string contentType)
    {
        if (bytes == null)
        {
            var notFound = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(notFound);
            return;
        }

        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            $"Cache-Control: public, max-age=31536000, immutable\r\n" +
            $"Connection: close\r\n\r\n");

        await stream.WriteAsync(header);
        await stream.WriteAsync(bytes);
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();

            var requestBuf = new byte[4096];
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var reqBuilder = new StringBuilder();
            
            while (DateTime.UtcNow < deadline)
            {
                if (stream.DataAvailable)
                {
                    int n = await stream.ReadAsync(requestBuf);
                    reqBuilder.Append(Encoding.ASCII.GetString(requestBuf, 0, n));
                    var req = reqBuilder.ToString();
                    if (req.Contains("\r\n\r\n") || req.Contains("\n\n"))
                        break;
                }
                else
                {
                    await Task.Delay(10);
                }
            }
            
            var fullReq = reqBuilder.ToString();
            var firstLine = fullReq.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];
            var reqParts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var rawPath = reqParts.Length > 1 ? reqParts[1] : "/";
            var path = rawPath.Split('?')[0];

            if (path == "/pdf.mjs")
            {
                await ServeBytesAsync(stream, s_pdfJsBytes.Value, "application/javascript; charset=utf-8");
                return;
            }

            if (path == "/pdf.worker.mjs")
            {
                await ServeBytesAsync(stream, s_pdfWorkerJsBytes.Value, "application/javascript; charset=utf-8");
                return;
            }

            if (path == "/reexport")
            {
                var queryIdx = rawPath.IndexOf('?');
                var queryStr = queryIdx != -1 ? rawPath.Substring(queryIdx + 1) : "";
                var parts = queryStr.Split('&');
                double darken = 1.0;
                foreach (var part in parts)
                {
                    if (part.StartsWith("darken="))
                        double.TryParse(part.Substring(7), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out darken);
                }
                
                if (OnReexportRequested != null)
                {
                    var base64 = await OnReexportRequested.Invoke(darken);
                    var bodyBytes = Encoding.UTF8.GetBytes(base64);
                    var okHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(okHeader);
                    await stream.WriteAsync(bodyBytes);
                    return;
                }
                var errHeader = Encoding.ASCII.GetBytes("HTTP/1.1 500 ERROR\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(errHeader);
                return;
            }

            var html = Volatile.Read(ref _currentHtml);
            var body = Encoding.UTF8.GetBytes(html);

            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\n" +
                $"Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                $"Connection: close\r\n" +
                $"\r\n");

            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
        }
        catch
        {
            // Client disconnected or other transient error — ignore
        }
        finally
        {
            client.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Stop();
    }
}
