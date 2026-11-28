using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SoundPair;

/// <summary>
/// Thread-safe persistent file logger writing to soundpair.log next to the executable.
/// Captures application lifecycle events, audio pipeline diagnostics, and unhandled crashes
/// in both Debug and Release builds.
/// </summary>
internal static class AppLogger
{
    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static bool _initialized;
    private const long MaxLogBytes = 2 * 1024 * 1024; // 2 MB rotation threshold

    public static string LogFilePath { get; private set; } = string.Empty;

    public static void Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                LogFilePath = Path.Combine(exeDir, "soundpair.log");

                // Rotate previous log if it exceeded 2 MB so disk usage stays bounded
                if (File.Exists(LogFilePath))
                {
                    var info = new FileInfo(LogFilePath);
                    if (info.Length > MaxLogBytes)
                    {
                        string prevPath = Path.Combine(exeDir, "soundpair.prev.log");
                        if (File.Exists(prevPath)) File.Delete(prevPath);
                        File.Move(LogFilePath, prevPath);
                    }
                }

                var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                // Route Trace.WriteLine (and Avalonia LogToTrace) into soundpair.log
                Trace.Listeners.Add(new FileTraceListener());

                Log($"=== SoundPair Started (v{typeof(AppLogger).Assembly.GetName().Version}, OS={Environment.OSVersion}) ===");
            }
            catch
            {
                // Never allow logging initialization failure to crash the app
            }
        }
    }

    public static void Log(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

        lock (_lock)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch { }
        }
    }

    public static void LogError(string context, Exception? ex)
    {
        if (ex == null)
        {
            Log($"[ERROR] {context}");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[ERROR] {context}: {ex.GetType().FullName}: {ex.Message}");
        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            sb.AppendLine(ex.StackTrace);
        }

        var inner = ex.InnerException;
        int depth = 0;
        while (inner != null && depth < 5)
        {
            sb.AppendLine($"  ---> Inner[{depth}]: {inner.GetType().FullName}: {inner.Message}");
            if (!string.IsNullOrEmpty(inner.StackTrace))
            {
                sb.AppendLine(inner.StackTrace);
            }
            inner = inner.InnerException;
            depth++;
        }

        Log(sb.ToString().TrimEnd());
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            try
            {
                _writer?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === SoundPair Exited Cleanly ===");
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
            }
            catch { }
        }
    }

    private sealed class FileTraceListener : TraceListener
    {
        public override void Write(string? message)
        {
            if (!string.IsNullOrEmpty(message))
                Log(message);
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrEmpty(message))
                Log(message);
        }
    }
}
