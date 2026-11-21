using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SoundPair
{
    public class MediaPlayerSyncService
    {
        #region Win32 Imports

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_COMMAND = 0x0111;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;

        #endregion

        #region Player Command Constants

        // PotPlayer WM_COMMAND IDs
        private const int POTPLAYER_CMD_AUDIO_SYNC_RESET = 10190;
        private const int POTPLAYER_CMD_AUDIO_SYNC_FASTER = 10191; // -0.05s (delays video relative to audio)
        private const int POTPLAYER_CMD_AUDIO_SYNC_SLOWER = 10192; // +0.05s

        // MPC-HC WM_COMMAND IDs
        private const int MPCHC_CMD_AUDIO_DELAY_PLUS = 905;   // +10ms
        private const int MPCHC_CMD_AUDIO_DELAY_MINUS = 906;  // -10ms (delays video relative to audio)
        private const int MPCHC_CMD_AUDIO_DELAY_RESET = 907;  // 0ms

        #endregion

        public event Action<string>? OnSyncStatusChanged;

        // Concurrency lock to prevent concurrent sync operations from colliding or freezing
        private readonly SemaphoreSlim _syncLock = new SemaphoreSlim(1, 1);

        // Process & Offset state tracking
        private int _syncedPid = 0;
        private string _syncedPlayerType = string.Empty;
        private int _syncedOffset = 0;

        // Cached VLC hotkeys to avoid constant disk I/O
        private char _cachedVlcDown = 'J';
        private char _cachedVlcUp = 'K';
        private long _lastVlcrcCheckTime = 0;

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };

        public bool IsAutoSyncEnabled { get; set; } = true;

        public async Task<string> TrySyncActivePlayersAsync(int offsetMs, bool force = false)
        {
            if (offsetMs < 0) offsetMs = 0;

            // Non-blocking concurrency check: if already syncing, skip this tick gracefully
            if (!await _syncLock.WaitAsync(0))
            {
                return "Syncing in progress...";
            }

            try
            {
                // Run on background thread to keep UI completely responsive
                return await Task.Run(async () =>
                {
                    // Fast-path: If an active player is already synced to this exact offset, check if it's still alive in <0.05ms
                    if (!force && _syncedPid > 0 && _syncedOffset == offsetMs)
                    {
                        if (IsProcessAlive(_syncedPid))
                        {
                            return $"🟢 Auto-Synced {_syncedPlayerType} (-{offsetMs} ms)";
                        }
                        else
                        {
                            _syncedPid = 0;
                            _syncedPlayerType = string.Empty;
                            _syncedOffset = 0;
                        }
                    }

                    // Find any active media player in a single, memory-safe pass
                    var (proc, playerType) = FindActivePlayerProcess();
                    if (proc == null)
                    {
                        _syncedPid = 0;
                        _syncedPlayerType = string.Empty;
                        _syncedOffset = 0;

                        string idle = offsetMs > 0
                            ? $"Waiting for media player (Offset: -{offsetMs} ms)"
                            : "No offset needed (0 ms)";
                        OnSyncStatusChanged?.Invoke(idle);
                        return idle;
                    }

                    using (proc)
                    {
                        bool success = false;
                        switch (playerType)
                        {
                            case "PotPlayer":
                                success = await TrySyncPotPlayerAsync(proc, offsetMs, force);
                                break;
                            case "VLC":
                                success = await TrySyncVlcAsync(proc, offsetMs, force);
                                break;
                            case "MPC-HC":
                                success = await TrySyncMpcHcAsync(proc, offsetMs, force);
                                break;
                            case "MPV":
                                success = await TrySyncMpvAsync(proc, offsetMs, force);
                                break;
                        }

                        if (success)
                        {
                            _syncedPid = proc.Id;
                            _syncedPlayerType = playerType;
                            _syncedOffset = offsetMs;

                            string status = $"🟢 Auto-Synced {playerType} (-{offsetMs} ms)";
                            OnSyncStatusChanged?.Invoke(status);
                            return status;
                        }
                    }

                    string fallbackStatus = offsetMs > 0
                        ? $"Waiting for media player (Offset: -{offsetMs} ms)"
                        : "No offset needed (0 ms)";
                    OnSyncStatusChanged?.Invoke(fallbackStatus);
                    return fallbackStatus;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaPlayerSync] Error: {ex.Message}");
                return "Auto-Sync: Ready";
            }
            finally
            {
                _syncLock.Release();
            }
        }

        #region Fast Process Enumeration & Disposal

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static readonly (string ProcessName, string PlayerType)[] TargetPlayers = new[]
        {
            ("PotPlayer64", "PotPlayer"),
            ("PotPlayer", "PotPlayer"),
            ("vlc", "VLC"),
            ("mpc-hc64", "MPC-HC"),
            ("mpc-hc", "MPC-HC"),
            ("mpc-be64", "MPC-HC"),
            ("mpc-be", "MPC-HC"),
            ("mpv", "MPV"),
            ("mpvnet", "MPV")
        };

        private static (Process? proc, string type) FindActivePlayerProcess()
        {
            foreach (var (procName, playerType) in TargetPlayers)
            {
                try
                {
                    var procs = Process.GetProcessesByName(procName);
                    if (procs.Length > 0)
                    {
                        var matched = procs[0];
                        for (int i = 1; i < procs.Length; i++)
                        {
                            try { procs[i].Dispose(); } catch { }
                        }
                        return (matched, playerType);
                    }
                }
                catch { }
            }

            return (null, string.Empty);
        }

        public void ResetSyncedTracking()
        {
            _syncedPid = 0;
            _syncedPlayerType = string.Empty;
            _syncedOffset = 0;
        }

        #endregion

        #region VLC Implementation

        private async Task<bool> TrySyncVlcAsync(Process vlcProc, int offsetMs, bool force)
        {
            int pid;
            IntPtr vlcHwnd = IntPtr.Zero;
            try
            {
                pid = vlcProc.Id;
                vlcHwnd = vlcProc.MainWindowHandle;
            }
            catch { return false; }

            if (!force && _syncedPid == pid && _syncedOffset == offsetMs)
            {
                return true;
            }

            double offsetSeconds = -(offsetMs / 1000.0);

            // Attempt 1: VLC HTTP Web Interface
            try
            {
                string url = $"http://127.0.0.1:8080/requests/status.json?command=audiodelay&val={offsetSeconds:F3}";
                using var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch { }

            // Attempt 2: Keystroke automation via Windows messages (non-blocking)
            if (vlcHwnd != IntPtr.Zero)
            {
                var (downKey, upKey) = GetVlcHotkeysCached();

                if (_syncedPid != pid || force)
                {
                    int targetSteps = (int)Math.Round(offsetMs / 50.0);
                    for (int i = 0; i < targetSteps; i++)
                    {
                        PostMessage(vlcHwnd, WM_KEYDOWN, (IntPtr)downKey, IntPtr.Zero);
                        await Task.Delay(15);
                        PostMessage(vlcHwnd, WM_KEYUP, (IntPtr)downKey, IntPtr.Zero);
                        await Task.Delay(10);
                    }
                }
                else
                {
                    int deltaMs = offsetMs - _syncedOffset;
                    int deltaSteps = (int)Math.Round(deltaMs / 50.0);

                    if (deltaSteps > 0)
                    {
                        for (int i = 0; i < deltaSteps; i++)
                        {
                            PostMessage(vlcHwnd, WM_KEYDOWN, (IntPtr)downKey, IntPtr.Zero);
                            await Task.Delay(15);
                            PostMessage(vlcHwnd, WM_KEYUP, (IntPtr)downKey, IntPtr.Zero);
                            await Task.Delay(10);
                        }
                    }
                    else if (deltaSteps < 0)
                    {
                        for (int i = 0; i < -deltaSteps; i++)
                        {
                            PostMessage(vlcHwnd, WM_KEYDOWN, (IntPtr)upKey, IntPtr.Zero);
                            await Task.Delay(15);
                            PostMessage(vlcHwnd, WM_KEYUP, (IntPtr)upKey, IntPtr.Zero);
                            await Task.Delay(10);
                        }
                    }
                }

                return true;
            }

            return false;
        }

        private (char downKey, char upKey) GetVlcHotkeysCached()
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastVlcrcCheckTime != 0 && (now - _lastVlcrcCheckTime) * 1000 / Stopwatch.Frequency < 30000)
            {
                return (_cachedVlcDown, _cachedVlcUp);
            }

            _lastVlcrcCheckTime = now;
            char down = 'J';
            char up = 'K';

            try
            {
                string vlcrcPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "vlc", "vlcrc");

                if (File.Exists(vlcrcPath))
                {
                    using var stream = new FileStream(vlcrcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("key-audiodelay-down="))
                        {
                            string val = trimmed.Substring("key-audiodelay-down=".Length).Trim();
                            if (!string.IsNullOrEmpty(val)) down = char.ToUpperInvariant(val[0]);
                        }
                        else if (trimmed.StartsWith("key-audiodelay-up="))
                        {
                            string val = trimmed.Substring("key-audiodelay-up=".Length).Trim();
                            if (!string.IsNullOrEmpty(val)) up = char.ToUpperInvariant(val[0]);
                        }
                    }
                }
            }
            catch { }

            _cachedVlcDown = down;
            _cachedVlcUp = up;
            return (down, up);
        }

        #endregion

        #region PotPlayer Implementation

        private async Task<bool> TrySyncPotPlayerAsync(Process potProc, int offsetMs, bool force)
        {
            int pid;
            IntPtr potHwnd = IntPtr.Zero;
            try
            {
                pid = potProc.Id;
                potHwnd = potProc.MainWindowHandle;
                if (potHwnd == IntPtr.Zero)
                {
                    potHwnd = FindWindow("PotPlayer64", null);
                    if (potHwnd == IntPtr.Zero) potHwnd = FindWindow("PotPlayer", null);
                }
            }
            catch { return false; }

            if (potHwnd == IntPtr.Zero) return false;

            if (!force && _syncedPid == pid && _syncedOffset == offsetMs)
            {
                return true;
            }

            // Step 1: Reset audio delay to 0 ms
            PostMessage(potHwnd, WM_COMMAND, (IntPtr)POTPLAYER_CMD_AUDIO_SYNC_RESET, IntPtr.Zero);
            await Task.Delay(25);

            // Step 2: Step audio faster (-0.05s / -50ms per step)
            int steps = (int)Math.Round(offsetMs / 50.0);
            for (int i = 0; i < steps; i++)
            {
                PostMessage(potHwnd, WM_COMMAND, (IntPtr)POTPLAYER_CMD_AUDIO_SYNC_FASTER, IntPtr.Zero);
                await Task.Delay(8);
            }

            return true;
        }

        #endregion

        #region MPC-HC / MPC-BE Implementation

        private async Task<bool> TrySyncMpcHcAsync(Process mpcProc, int offsetMs, bool force)
        {
            int pid;
            IntPtr mpcHwnd = IntPtr.Zero;
            try
            {
                pid = mpcProc.Id;
                mpcHwnd = mpcProc.MainWindowHandle;
                if (mpcHwnd == IntPtr.Zero)
                {
                    mpcHwnd = FindWindow("MediaPlayerClassicW", null);
                }
            }
            catch { return false; }

            if (mpcHwnd == IntPtr.Zero) return false;

            if (!force && _syncedPid == pid && _syncedOffset == offsetMs)
            {
                return true;
            }

            // Reset delay to 0
            PostMessage(mpcHwnd, WM_COMMAND, (IntPtr)MPCHC_CMD_AUDIO_DELAY_RESET, IntPtr.Zero);
            await Task.Delay(20);

            // 10ms steps for MPC-HC
            int steps = (int)Math.Round(offsetMs / 10.0);
            for (int i = 0; i < steps; i++)
            {
                PostMessage(mpcHwnd, WM_COMMAND, (IntPtr)MPCHC_CMD_AUDIO_DELAY_MINUS, IntPtr.Zero);
                await Task.Delay(4);
            }

            return true;
        }

        #endregion

        #region MPV Implementation

        private async Task<bool> TrySyncMpvAsync(Process mpvProc, int offsetMs, bool force)
        {
            int pid;
            try { pid = mpvProc.Id; } catch { return false; }

            if (!force && _syncedPid == pid && _syncedOffset == offsetMs)
            {
                return true;
            }

            string[] possiblePipes = { "mpvsocket", "mpv", "mpv-pipe" };
            double offsetSeconds = -(offsetMs / 1000.0);

            foreach (var pipeName in possiblePipes)
            {
                try
                {
                    using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                    await pipeClient.ConnectAsync(80);
                    using var writer = new StreamWriter(pipeClient, Encoding.UTF8) { AutoFlush = true };
                    string jsonCmd = $"{{\"command\": [\"set_property\", \"audio-delay\", {offsetSeconds:F3}]}}\n";
                    await writer.WriteAsync(jsonCmd);

                    return true;
                }
                catch { }
            }

            return false;
        }

        #endregion
    }
}
