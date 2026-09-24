using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NoteinDesktopViewer;

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

    public event Action<double>? OnReexportRequested;

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
            if (fullReq.StartsWith("GET /reexport?darken="))
            {
                var endIdx = fullReq.IndexOf(' ', 21);
                if (endIdx != -1)
                {
                    var valStr = fullReq.Substring(21, endIdx - 21);
                    if (double.TryParse(valStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double val))
                    {
                        OnReexportRequested?.Invoke(val);
                    }
                }
                var okHeader = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(okHeader);
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
