using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NoteinDesktopViewer;

/// <summary>
/// Minimal in-process HTTP server that serves a single HTML page to the WebView.
/// Using localhost instead of file:// avoids WebKitGTK's restriction that blocks
/// loading external (CDN) scripts from file:// origins on Linux.
/// </summary>
internal sealed class PdfViewerServer : IDisposable
{
    private readonly HttpListener _listener;
    private string _currentHtml = "<html><body>No content</body></html>";
    private bool _disposed;

    public int Port { get; }

    public PdfViewerServer()
    {
        Port = FindFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        Task.Run(ServeLoopAsync);
    }

    /// <summary>Updates the HTML that will be served on the next (or current) request.</summary>
    public void SetContent(string html)
    {
        Volatile.Write(ref _currentHtml, html);
    }

    private async Task ServeLoopAsync()
    {
        while (!_disposed)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break; // Listener was stopped
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var html = Volatile.Read(ref _currentHtml);
            var bytes = Encoding.UTF8.GetBytes(html);

            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            try
            {
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch
            {
                // Client disconnected — ignore
            }
            finally
            {
                ctx.Response.Close();
            }
        }
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Stop();
        _listener.Close();
    }
}
