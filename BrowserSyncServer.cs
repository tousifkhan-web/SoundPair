using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoundPair
{
    /// <summary>
    /// BrowserSyncServer is a lightweight, embedded loopback HTTP server.
    /// 
    /// Purpose:
    /// In multi-headset streaming (especially over Bluetooth), audio latency typically ranges
    /// from 80ms to 250ms. When users watch videos on YouTube, Netflix, or web-based media players,
    /// audio can arrive slightly later than video (lip-sync mismatch).
    /// 
    /// How it works:
    /// 1. Listens on http://127.0.0.1:48888 (with automatic port increment fallback if occupied).
    /// 2. SoundPair's Companion Browser Extension (Chrome/Edge/Firefox) queries GET /offset or /status.
    /// 3. Returns the master calibrated Global Latency Offset in JSON: { "offsetMs": 150, "isStreaming": true }.
    /// 4. The extension automatically delays HTML5 <video> frames by that exact offset, providing flawless lip-sync.
    /// 5. Full Cross-Origin Resource Sharing (CORS) headers are included to support all browser extensions.
    /// </summary>
    public class BrowserSyncServer : IDisposable
    {
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private int _port = 48888;
        private volatile bool _isRunning = false;
        private readonly object _lock = new object();

        private volatile int _currentOffsetMs = 0;
        private volatile bool _isStreaming = false;

        /// <summary>
        /// Current global video-delay offset in milliseconds to broadcast to the browser extension.
        /// </summary>
        public int CurrentOffsetMs
        {
            get => _currentOffsetMs;
            set => _currentOffsetMs = value;
        }

        /// <summary>
        /// Streaming state flag indicating whether SoundPair is actively outputting audio.
        /// </summary>
        public bool IsStreaming
        {
            get => _isStreaming;
            set => _isStreaming = value;
        }

        /// <summary>
        /// Active desktop or web media player status description.
        /// </summary>
        public string ActivePlayerStatus { get; set; } = "None";

        public int Port => _port;
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Starts the HTTP loopback listener on the preferred port.
        /// If the port is in use, it attempts up to 3 fallback ports sequentially (e.g. 48888, 48889, 48890).
        /// </summary>
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

                        // Spawn background listen loop
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

        /// <summary>
        /// Asynchronous listen loop that accepts incoming HTTP requests without blocking threads.
        /// </summary>
        private async Task ListenLoopAsync(HttpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested && _isRunning)
            {
                try
                {
                    if (!listener.IsListening) break;

                    var context = await listener.GetContextAsync();
                    // Process each request asynchronously on thread pool
                    _ = Task.Run(() => HandleRequestSafe(context));
                }
                catch (HttpListenerException)
                {
                    // Normal during server shutdown or port close
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

        /// <summary>
        /// Handles incoming HTTP GET / OPTIONS requests and replies with JSON sync data.
        /// </summary>
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

                // Handle CORS pre-flight OPTIONS request
                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = (int)HttpStatusCode.NoContent;
                    return;
                }

                string path = req.Url?.AbsolutePath.ToLowerInvariant() ?? "/";

                // Return sync offset and status
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
                // Always close response stream to prevent unmanaged socket handle leaks
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

        /// <summary>
        /// Stops the HTTP listener and releases network resources.
        /// </summary>
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
