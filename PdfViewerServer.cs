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

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();

            // Drain the incoming HTTP request (we don't care about its content)
            var requestBuf = new byte[4096];
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (stream.DataAvailable)
                {
                    int n = await stream.ReadAsync(requestBuf);
                    // Stop once we've seen the blank line that ends the HTTP headers
                    var req = Encoding.ASCII.GetString(requestBuf, 0, n);
                    if (req.Contains("\r\n\r\n") || req.Contains("\n\n"))
                        break;
                }
                else
                {
                    await Task.Delay(10);
                }
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
