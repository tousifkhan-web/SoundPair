using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoundPair
{
    public class BrowserSyncServer : IDisposable
    {
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private int _port = 48888;
        private volatile bool _isRunning = false;
        private readonly object _lock = new object();

        public int CurrentOffsetMs { get; set; } = 0;
        public bool IsStreaming { get; set; } = false;
        public string ActivePlayerStatus { get; set; } = "None";

        public int Port => _port;
        public bool IsRunning => _isRunning;

        public void Start(int preferredPort = 48888)
        {
            lock (_lock)
            {
                if (_isRunning) return;

                _port = preferredPort;
                _cts = new CancellationTokenSource();

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        var listener = new HttpListener();
                        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                        listener.Start();

                        _listener = listener;
                        _isRunning = true;
                        Debug.WriteLine($"[BrowserSyncServer] Started on http://127.0.0.1:{_port}/");

                        Task.Run(() => ListenLoopAsync(listener, _cts.Token));
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[BrowserSyncServer] Failed port {_port}: {ex.Message}");
                        _port++;
                    }
                }
            }
        }

        private async Task ListenLoopAsync(HttpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested && _isRunning)
            {
                try
                {
                    // If listener is stopping, safely exit
                    if (!listener.IsListening) break;

                    var context = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestSafe(context));
                }
                catch (HttpListenerException)
                {
                    // Normal during shutdown/port close
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Normal during Stop() / Dispose()
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BrowserSyncServer] Listen exception: {ex.Message}");
                    if (!_isRunning || token.IsCancellationRequested) break;
                    await Task.Delay(200, CancellationToken.None);
                }
            }
        }

        private void HandleRequestSafe(HttpListenerContext context)
        {
            HttpListenerResponse? res = null;
            try
            {
                var req = context.Request;
                res = context.Response;

                // Add CORS headers for browser extension compatibility
                res.Headers.Add("Access-Control-Allow-Origin", "*");
                res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = (int)HttpStatusCode.NoContent;
                    return;
                }

                string path = req.Url?.AbsolutePath.ToLowerInvariant() ?? "/";

                if (path == "/" || path == "/offset" || path == "/status")
                {
                    res.ContentType = "application/json; charset=utf-8";
                    res.StatusCode = (int)HttpStatusCode.OK;

                    string json = $"{{\"status\":\"ok\",\"offsetMs\":{CurrentOffsetMs},\"isStreaming\":{(IsStreaming ? "true" : "false")},\"activePlayer\":\"{EscapeJson(ActivePlayerStatus)}\",\"port\":{_port}}}";
                    byte[] buffer = Encoding.UTF8.GetBytes(json);
                    res.ContentLength64 = buffer.Length;
                    res.OutputStream.Write(buffer, 0, buffer.Length);
                }
                else
                {
                    res.StatusCode = (int)HttpStatusCode.NotFound;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BrowserSyncServer] HandleRequest error: {ex.Message}");
            }
            finally
            {
                // Always close response to prevent unmanaged connection leaks
                try
                {
                    res?.Close();
                }
                catch { }
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning) return;
                _isRunning = false;

                try
                {
                    _cts?.Cancel();
                }
                catch { }

                try
                {
                    if (_listener != null)
                    {
                        if (_listener.IsListening)
                        {
                            _listener.Stop();
                        }
                        _listener.Close();
                    }
                }
                catch { }
                finally
                {
                    _listener = null;
                }
            }
        }

        public void Dispose()
        {
            Stop();
            try { _cts?.Dispose(); } catch { }
        }
    }
}
