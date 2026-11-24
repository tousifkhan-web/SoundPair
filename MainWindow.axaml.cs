using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundPair
{
    /// <summary>
    /// MainWindow is the core orchestrator of SoundPair (Tri Mode).
    /// 
    /// Architecture Overview:
    /// 1. CAPTURE ENGINE: Captures pre-mute system audio using Windows 11 Process Loopback
    ///    (WasapiRecorder with ExcludeTargetProcessTree), allowing the PC speaker to be
    ///    muted to prevent local room echo while preserving full system audio stream.
    /// 2. STREAM ROUTING & BUFFERING: Duplicates captured IEEE Float 48kHz audio into up to 3
    ///    independent BufferedWaveProviders. Each slot runs its own sample channel, volume scaler,
    ///    and high-performance WDL resampler targeting each output device's native format.
    /// 3. DUAL-PAIRING (MULTIPOINT) CALL ISOLATION:
    ///    Provides dedicated Pause/Resume buttons per slot. When one dual-pairing headset switches
    ///    to a phone call, pausing that slot immediately stops WASAPI audio frames, preventing
    ///    Bluetooth ARQ retry storms and saving 100% of PC radio bandwidth for the other headset.
    /// 4. ZERO-DRIFT SYNC GUARANTEE:
    ///    When unpausing or starting streams, buffers are flushed and pre-filled with calibrated
    ///    silence padding, ensuring sub-millisecond audio alignment with zero accumulated latency drift.
    /// 5. AUTO VIDEO SYNC (MILESTONE 2):
    ///    Broadcasting global audio latency offset to browser extensions via an embedded HTTP server
    ///    and desktop media players (PotPlayer, VLC, MPC-HC, MPV) via IPC / Win32 messages.
    /// </summary>
    public partial class MainWindow : Window
    {
        // --- Core Audio Capture & Output Pipeline ---
        private WasapiRecorder? capture;                                      // Native WASAPI process loopback capture
        private BufferedWaveProvider? buffer1, buffer2, buffer3;              // Independent FIFO audio ring buffers per slot
        private SampleChannel? sampleChannel1, sampleChannel2, sampleChannel3;// Per-slot floating point gain / volume processors
        private WasapiPlayer? out1, out2, out3;                               // Low-latency WASAPI render clients per output device

        private readonly List<MMDevice> _cachedDevices = new();               // Cached CoreAudio device enumeration
        private DispatcherTimer? _delayDebounceTimer;                         // Debounce timer for slider latency adjustments
        private MMDevice? defaultRenderDevice;                                // Original default Windows audio endpoint

        // Hardware volume synchronization states (bi-directional sync between app sliders & OS master volume)
        private MMDevice? boundDevice1, boundDevice2, boundDevice3;
        private bool _isSyncingVolume1, _isSyncingVolume2, _isSyncingVolume3;
        private long _lastUserVol1ChangeTime, _lastUserVol2ChangeTime, _lastUserVol3ChangeTime;

        // Streaming state flags
        private bool isStreaming = false;
        private bool isInitialLoad = true;

        // Delay offsets (in milliseconds)
        private int delay1Ms = 0;
        private int delay2Ms = 0;
        private int delay3Ms = 0;
        private int globalOffsetMs = 0;                                       // Master latency offset applied to video sync

        private const int WM_DEVICECHANGE = 0x0219;                           // Win32 device change notification message

        private float cachedSysVol = 1.0f;                                    // Cached master system volume level
        private string? activeId1, activeId2, activeId3;                      // Active MMDevice endpoint GUIDs
        // Stall detection states: Set to true when an output endpoint fails or device is unplugged
        private volatile bool _slot1Stalled = false;
        private volatile bool _slot2Stalled = false;
        private volatile bool _slot3Stalled = false;

        // Dynamic buffer capacity limits (in bytes) to prevent infinite memory queuing and latency drift
        private volatile int _maxBacklogBytes1 = int.MaxValue;
        private volatile int _maxBacklogBytes2 = int.MaxValue;
        private volatile int _maxBacklogBytes3 = int.MaxValue;

        // Device transport classification (Wireless/Bluetooth vs Wired)
        private bool _isSlot1Wireless = false;
        private bool _isSlot2Wireless = false;
        private bool _isSlot3Wireless = false;

        // PC Speaker silence state preservation for local room echo cancellation
        private bool _originalSpeakerMuteState = false;
        private bool _isSyncingSpeakerMute = false;
        private string? originalDefaultDeviceId = null;
        private bool didChangeDefaultDevice = false;

        // Dual-Pairing (Multipoint) Call Pause & Resume States:
        // When a user receives a phone call on a dual-paired headset, they click Pause on that slot.
        // Pausing immediately cuts audio transmission to WASAPI and flushes the buffer, completely
        // halting Bluetooth packet transmission and preventing ARQ retry storms. This guarantees
        // 100% of the PC's Bluetooth radio airtime remains dedicated to the other active headset.
        private volatile bool _isSlot1UserPaused = false;
        private volatile bool _isSlot2UserPaused = false;
        private volatile bool _isSlot3UserPaused = false;

        // UI Brushes for the Pause / Resume slot buttons
        private static readonly IBrush PausedButtonBg = new SolidColorBrush(Color.FromRgb(180, 83, 9)); // Amber / Warning
        private static readonly IBrush PausedButtonBorder = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        private static readonly IBrush ActiveButtonBg = new SolidColorBrush(Color.FromRgb(56, 56, 56)); // Standard dark grey
        private static readonly IBrush ActiveButtonBorder = new SolidColorBrush(Color.FromRgb(74, 74, 74));

        private TrayIcon? _trayIcon;
        private DispatcherTimer? debounceTimer;
        private static readonly byte[] SilenceBuffer = new byte[1048576]; // 1MB pre-allocated zero buffer for fast zero-drift padding

        // Synchronization lock for real-time audio buffer operations across UI & Audio threads
        private readonly object _bufferLock = new object();

        // Persistent Device Profiles
        private string profilesFilePath = string.Empty;
        private Dictionary<string, int> deviceProfiles = new(StringComparer.OrdinalIgnoreCase);

        // Auto Video Sync (Milestone 2: Media Player & Browser Sync)
        private readonly MediaPlayerSyncService _mediaPlayerSyncService = new();
        private readonly BrowserSyncServer _browserSyncServer = new();
        private DispatcherTimer? _mediaPlayerWatcherTimer;

        // Visual Calibration Studio
        private bool isVisualCalibrating = false;
        private int _calActiveSlot = 0; // 0 = Global, 1 = Slot 1, 2 = Slot 2, 3 = Slot 3
        private WasapiPlayer? _calPlayer;
        private CalibrationClickProvider? _calClickProvider;
        private readonly List<(WasapiPlayer player, CalibrationClickProvider provider, int slotIndex, bool isWireless)> _globalCalOutputs = new();
        private readonly Stopwatch _dropStopwatch = new Stopwatch();
        private DispatcherTimer? _autoDropTimer;
        private bool _isDropping = false;
        private bool _hasImpacted = false;
        private bool _awaitingTap = false;
        private bool _spacebarDown = false; // tracks physical key-hold to suppress auto-repeat
        private long _impactTimestamp = 0;
        private readonly List<double> _recordedRawTaps = new List<double>();
        private int _nudgeOffsetMs = 0;
        private const double DropDurationMs = 850.0;
        private const double StartY = 30.0;
        private const double TargetY = 114.0;
        private const double BallDiameter = 22.0;

        // Ripple Animation state
        private bool _rippleActive = false;
        private long _rippleStartTimestamp = 0;

        // Side-panel animation state
        private readonly Stopwatch _panelAnimStopwatch = new Stopwatch();
        private bool _isPanelAnimating = false;
        private bool _panelAnimOpening = false;
        private bool _isFirstAnimFrame = false;
        private double _animStartWidth = 470.0;
        private double _animTargetWidth = 920.0;
        private const double PanelAnimDurationMs = 260.0;

        // Brushes
        private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF));
        private static readonly IBrush TextSecBrush = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0));
        private static readonly IBrush TabActiveBg = new SolidColorBrush(Color.FromRgb(36, 52, 71));
        private static readonly IBrush TabIdleBg = new SolidColorBrush(Color.FromRgb(46, 46, 46));
        private static readonly IBrush TabIdleFg = new SolidColorBrush(Color.FromRgb(170, 170, 170));

        // Win32 Subclassing
        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);
        private SubclassProc? _subclassProc;
        private IntPtr _hwnd = IntPtr.Zero;

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        private const uint MB_OK = 0x00000000;
        private const uint MB_ICONWARNING = 0x00000030;
        private const uint MB_ICONERROR = 0x00000010;

        public MainWindow()
        {
            InitializeComponent();

            if (Design.IsDesignMode)
            {
                // In visual designer / preview mode, supply mock device data without calling hardware COM/audio APIs
                comboDevice1.Items.Add("[0] Headphones (OnePlus Nord Buds 4 Pro)");
                comboDevice2.Items.Add("-- Disabled / Blank --");
                comboDevice3.Items.Add("-- Disabled / Blank --");
                comboDevice1.SelectedIndex = 0;
                comboDevice2.SelectedIndex = 0;
                comboDevice3.SelectedIndex = 0;
                return;
            }

            InitializeTrayIcon();

            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string soundPairFolder = Path.Combine(appData, "SoundPair");
                Directory.CreateDirectory(soundPairFolder);
                profilesFilePath = Path.Combine(soundPairFolder, "device_profiles.json");
            }
            catch
            {
                profilesFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "device_profiles.json");
            }
            LoadDeviceProfiles();

            comboDevice1.SelectionChanged += ComboDevice1_SelectionChanged;
            comboDevice2.SelectionChanged += ComboDevice2_SelectionChanged;
            comboDevice3.SelectionChanged += ComboDevice3_SelectionChanged;

            // Tunnel KeyDown — Space is intercepted before any child button can consume it.
            // KeyUp and fallback OnKeyDown/OnKeyUp are handled via overrides below.
            AddHandler(InputElement.KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);

            SwitchToInternalSpeakersOnStartup();
            LoadAudioDevices();

            // Initialize Auto-Sync Services (Milestone 2)
            _mediaPlayerSyncService.OnSyncStatusChanged += (status) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (lblPlayerSyncStatus != null)
                    {
                        lblPlayerSyncStatus.Text = status;
                        lblPlayerSyncStatus.Foreground = status.StartsWith("🟢")
                            ? Brushes.LightGreen
                            : new SolidColorBrush(Color.FromRgb(160, 174, 192));
                    }
                });
            };

            _browserSyncServer.Start(48888);

            // Periodic auto-sync timer for desktop media players (VLC, PotPlayer, MPC-HC, MPV)
            _mediaPlayerWatcherTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _mediaPlayerWatcherTimer.Tick += async (_, _) =>
            {
                try
                {
                    if (isStreaming && globalOffsetMs > 0 && _mediaPlayerSyncService.IsAutoSyncEnabled)
                    {
                        await _mediaPlayerSyncService.TrySyncActivePlayersAsync(globalOffsetMs);
                    }
                }
                catch { }
            };
            // Timer starts only when streaming starts, saving system resources when idle
            // _mediaPlayerWatcherTimer.Start();

            // Lock initial window width to 470 so Column 1 (450px) is clipped off-screen until opened
            this.MaxWidth = 470.0;
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (Design.IsDesignMode) return;
            HookWndProc();
            ApplyDarkTitleBar();
        }

        private void HookWndProc()
        {
            var handle = this.TryGetPlatformHandle();
            if (handle != null && handle.Handle != IntPtr.Zero)
            {
                _hwnd = handle.Handle;
                _subclassProc = new SubclassProc(WndProcSubclass);
                SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)101, UIntPtr.Zero);
            }
        }

        private IntPtr WndProcSubclass(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (uMsg == WM_DEVICECHANGE)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (debounceTimer == null)
                    {
                        debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                        debounceTimer.Tick += (s, args) =>
                        {
                            debounceTimer?.Stop();
                            if (isStreaming)
                            {
                                HandleDeviceChangeWhileStreaming();
                            }
                            else
                            {
                                LoadAudioDevices();
                            }
                        };
                    }
                    debounceTimer.Stop();
                    debounceTimer.Start();
                });
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void ApplyDarkTitleBar()
        {
            if (_hwnd != IntPtr.Zero)
            {
                int useImmersiveDarkMode = 1;
                int hr = DwmSetWindowAttribute(_hwnd, 20, ref useImmersiveDarkMode, sizeof(int));
                if (hr != 0)
                {
                    DwmSetWindowAttribute(_hwnd, 19, ref useImmersiveDarkMode, sizeof(int));
                }
            }
        }

        private void ShowWarning(string message, string title = "Warning")
        {
            var h = _hwnd != IntPtr.Zero ? _hwnd : IntPtr.Zero;
            MessageBoxW(h, message, title, MB_OK | MB_ICONWARNING);
        }

        private void ShowError(string message, string title = "Error")
        {
            var h = _hwnd != IntPtr.Zero ? _hwnd : IntPtr.Zero;
            MessageBoxW(h, message, title, MB_OK | MB_ICONERROR);
        }

        private void InitializeTrayIcon()
        {
            try
            {
                _trayIcon = new TrayIcon
                {
                    ToolTipText = "Sound Pair",
                    IsVisible = false
                };

                if (this.Icon != null)
                {
                    _trayIcon.Icon = this.Icon;
                }

                var menu = new NativeMenu();
                var openItem = new NativeMenuItem("Open");
                openItem.Click += (s, e) => RestoreFromTray();
                var exitItem = new NativeMenuItem("Exit");
                exitItem.Click += (s, e) => Close();

                menu.Items.Add(openItem);
                menu.Items.Add(exitItem);

                _trayIcon.Menu = menu;
                _trayIcon.Clicked += (s, e) => RestoreFromTray();
            }
            catch { }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == WindowStateProperty)
            {
                if (WindowState == WindowState.Minimized)
                {
                    this.Hide();
                    if (_trayIcon != null)
                    {
                        _trayIcon.IsVisible = true;
                    }
                }
            }
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            if (_trayIcon != null)
            {
                _trayIcon.IsVisible = false;
            }
        }

        #region Device Profiles (Memory)

        private void LoadDeviceProfiles()
        {
            try
            {
                if (File.Exists(profilesFilePath))
                {
                    string json = File.ReadAllText(profilesFilePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    if (dict != null)
                    {
                        deviceProfiles = new Dictionary<string, int>(dict, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch { }
        }

        private void SaveDeviceProfile(string deviceName, int latencyMs)
        {
            try
            {
                string pureName = GetPureDeviceName(deviceName) ?? deviceName;
                deviceProfiles[pureName] = latencyMs;
                deviceProfiles[deviceName] = latencyMs;
                SaveDeviceProfilesFile();
            }
            catch { }
        }

        private void SaveDeviceDelay(string deviceName, int delayMs)
        {
            try
            {
                string pureName = GetPureDeviceName(deviceName) ?? deviceName;
                deviceProfiles[$"delay_{pureName}"] = delayMs;
                deviceProfiles[$"delay_{deviceName}"] = delayMs;
                SaveDeviceProfilesFile();
            }
            catch { }
        }

        private int? GetSavedDeviceDelay(string? rawDeviceName)
        {
            if (string.IsNullOrEmpty(rawDeviceName) || rawDeviceName.StartsWith("--")) return null;
            string pure = GetPureDeviceName(rawDeviceName) ?? rawDeviceName;

            if (deviceProfiles.TryGetValue($"delay_{pure}", out int d1)) return d1;
            if (deviceProfiles.TryGetValue($"delay_{rawDeviceName}", out int d2)) return d2;

            foreach (var kvp in deviceProfiles)
            {
                if (kvp.Key.StartsWith("delay_", StringComparison.OrdinalIgnoreCase))
                {
                    string keyName = kvp.Key.Substring(6);
                    if (keyName.Length >= 6 && pure.Length >= 6 &&
                        (pure.Contains(keyName, StringComparison.OrdinalIgnoreCase) ||
                         keyName.Contains(pure, StringComparison.OrdinalIgnoreCase)))
                    {
                        return kvp.Value;
                    }
                }
            }
            return null;
        }

        private void ClearDeviceProfile(string deviceName)
        {
            try
            {
                string pure = GetPureDeviceName(deviceName) ?? deviceName;
                deviceProfiles.Remove(pure);
                deviceProfiles.Remove(deviceName);
                deviceProfiles.Remove($"delay_{pure}");
                deviceProfiles.Remove($"delay_{deviceName}");

                var toRemove = deviceProfiles.Keys.Where(k =>
                    k.Equals(pure, StringComparison.OrdinalIgnoreCase) ||
                    k.Equals(deviceName, StringComparison.OrdinalIgnoreCase) ||
                    k.Equals($"delay_{pure}", StringComparison.OrdinalIgnoreCase) ||
                    k.Equals($"delay_{deviceName}", StringComparison.OrdinalIgnoreCase) ||
                    (k.Length >= 6 && pure.Length >= 6 && (pure.Contains(k, StringComparison.OrdinalIgnoreCase) || k.Contains(pure, StringComparison.OrdinalIgnoreCase)))).ToList();

                foreach (var key in toRemove)
                {
                    deviceProfiles.Remove(key);
                }

                SaveDeviceProfilesFile();
            }
            catch { }
        }

        private void SaveDeviceProfilesFile()
        {
            try
            {
                string json = JsonSerializer.Serialize(deviceProfiles, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(profilesFilePath, json);
            }
            catch { }
        }

        private int? GetSavedProfileLatency(string? rawDeviceName)
        {
            if (string.IsNullOrEmpty(rawDeviceName) || rawDeviceName.StartsWith("--")) return null;
            string pure = GetPureDeviceName(rawDeviceName) ?? rawDeviceName;

            if (deviceProfiles.TryGetValue(pure, out int lat1)) return lat1;
            if (deviceProfiles.TryGetValue(rawDeviceName, out int lat2)) return lat2;

            foreach (var kvp in deviceProfiles)
            {
                if (kvp.Key.Length >= 6 && pure.Length >= 6 &&
                    (pure.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                     kvp.Key.Contains(pure, StringComparison.OrdinalIgnoreCase)))
                {
                    return kvp.Value;
                }
            }
            return null;
        }

        private void TryAutoLoadProfilesForActiveDevices(bool forceEvenIfStreaming = false)
        {
            if ((isStreaming && !forceEvenIfStreaming) || isInitialLoad || _isDropping) return;

            var dev1 = GetDeviceFromCombo(comboDevice1);
            var dev2 = GetDeviceFromCombo(comboDevice2);
            var dev3 = GetDeviceFromCombo(comboDevice3);

            int? d1 = dev1 != null ? GetSavedDeviceDelay(dev1.FriendlyName) : null;
            int? d2 = dev2 != null ? GetSavedDeviceDelay(dev2.FriendlyName) : null;
            int? d3 = dev3 != null ? GetSavedDeviceDelay(dev3.FriendlyName) : null;

            if (d1.HasValue || d2.HasValue || d3.HasValue)
            {
                int delay1 = d1 ?? 0;
                int delay2 = d2 ?? 0;
                int delay3 = d3 ?? 0;
                int maxDelay = Math.Max(delay1, Math.Max(delay2, delay3));
                UpdateGlobalOffset(maxDelay);

                SetSlotDelay(1, delay1, applyIfStreaming: false);
                SetSlotDelay(2, delay2, applyIfStreaming: false);
                SetSlotDelay(3, delay3, applyIfStreaming: false);

                if (lblStatus != null)
                {
                    lblStatus.Text = $"Status: Loaded saved delays (Slot 1: {delay1}ms, Slot 2: {delay2}ms, Slot 3: {delay3}ms)";
                    lblStatus.Foreground = Brushes.LightGreen;
                }
                UpdateCalibrationDisplayForCurrentSlot();
                return;
            }

            int? l1 = dev1 != null ? GetSavedProfileLatency(dev1.FriendlyName) : null;
            int? l2 = dev2 != null ? GetSavedProfileLatency(dev2.FriendlyName) : null;
            int? l3 = dev3 != null ? GetSavedProfileLatency(dev3.FriendlyName) : null;

            var valid = new List<int>();
            if (l1.HasValue) valid.Add(l1.Value);
            if (l2.HasValue) valid.Add(l2.Value);
            if (l3.HasValue) valid.Add(l3.Value);

            if (valid.Count >= 2)
            {
                int maxLat = valid.Max();
                UpdateGlobalOffset(maxLat);

                SetSlotDelay(1, l1.HasValue ? Math.Max(0, maxLat - l1.Value) : 0, applyIfStreaming: false);
                SetSlotDelay(2, l2.HasValue ? Math.Max(0, maxLat - l2.Value) : 0, applyIfStreaming: false);
                SetSlotDelay(3, l3.HasValue ? Math.Max(0, maxLat - l3.Value) : 0, applyIfStreaming: false);

                if (lblStatus != null)
                {
                    lblStatus.Text = $"Status: Balanced delays from saved profiles (Max: {maxLat} ms)";
                    lblStatus.Foreground = Brushes.LightGreen;
                }
            }
            else if (valid.Count == 1)
            {
                int singleLat = valid[0];
                UpdateGlobalOffset(singleLat);

                SetSlotDelay(1, 0, applyIfStreaming: false);
                SetSlotDelay(2, 0, applyIfStreaming: false);
                SetSlotDelay(3, 0, applyIfStreaming: false);

                if (lblStatus != null)
                {
                    lblStatus.Text = $"Status: Loaded saved profile ({singleLat} ms)";
                    lblStatus.Foreground = Brushes.LightGreen;
                }
            }
            else
            {
                UpdateGlobalOffset(0);
                SetSlotDelay(1, 0, applyIfStreaming: false);
                SetSlotDelay(2, 0, applyIfStreaming: false);
                SetSlotDelay(3, 0, applyIfStreaming: false);
            }

            UpdateCalibrationDisplayForCurrentSlot();
        }

        #endregion

        #region Default Device Management

        private bool IsVbCable(MMDevice? dev)
        {
            if (dev == null) return false;
            string name = dev.FriendlyName;
            return name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);
        }

        private MMDevice? FindInternalSpeakersDevice(IEnumerable<MMDevice> devices)
        {
            MMDevice? fallback = null;

            foreach (var dev in devices)
            {
                if (dev == null) continue;
                string name = dev.FriendlyName;

                if (IsVbCable(dev)) continue;
                if (IsWirelessDevice(dev)) continue;

                // Priority 1: Built-in laptop speakers
                if (name.Contains("Speaker", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Internal", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Built-in", StringComparison.OrdinalIgnoreCase))
                {
                    return dev;
                }

                // Priority 2: Standard internal audio hardware chipset
                if (fallback == null && (
                    name.Contains("Senary", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase)))
                {
                    fallback = dev;
                }
            }

            return fallback;
        }

        private void SwitchToInternalSpeakersOnStartup()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var defaultDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                if (defaultDev != null && !IsVbCable(defaultDev))
                {
                    originalDefaultDeviceId = defaultDev.ID;
                }

                var speakerDev = FindInternalSpeakersDevice(endpoints);
                if (speakerDev != null)
                {
                    if (defaultDev != null && string.Equals(defaultDev.ID, speakerDev.ID, StringComparison.OrdinalIgnoreCase))
                    {
                        didChangeDefaultDevice = false;
                        Debug.WriteLine($"[Default Device] Already set to internal speakers: {speakerDev.FriendlyName}");
                    }
                    else
                    {
                        if (SetSystemDefaultAudioDevice(speakerDev.ID))
                        {
                            didChangeDefaultDevice = true;
                            Debug.WriteLine($"[Default Device] Auto-selected internal speakers: {speakerDev.FriendlyName} (will restore on close)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Default Device Switch Error] {ex.Message}");
            }
        }

        public void RestoreOriginalDefaultDevice()
        {
            try
            {
                if (didChangeDefaultDevice && !string.IsNullOrEmpty(originalDefaultDeviceId))
                {
                    SetSystemDefaultAudioDevice(originalDefaultDeviceId);
                    didChangeDefaultDevice = false;
                    Debug.WriteLine($"[Default Device] Restored original system playback device: {originalDefaultDeviceId}");
                }
            }
            catch { }
        }

        public static bool SetSystemDefaultAudioDevice(string deviceId)
        {
            try
            {
                var policyConfig = (IPolicyConfig)new PolicyConfigClient();
                policyConfig.SetDefaultEndpoint(deviceId, Role.Multimedia);
                policyConfig.SetDefaultEndpoint(deviceId, Role.Console);
                policyConfig.SetDefaultEndpoint(deviceId, Role.Communications);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        /// <summary>
        /// Base pre-fill silence padding in milliseconds.
        /// <summary>
        /// Wireless (Bluetooth) headsets require ~200ms baseline padding (matching the 150ms WASAPI hardware
        /// buffer + 50ms jitter cushion) to absorb RF TDD packet bursts and prevent starvation glitches,
        /// while wired endpoints run reliably with ~70ms padding.
        /// </summary>
        private static int GetBasePaddingMs(bool isWireless) => isWireless ? 200 : 70;

        /// <summary>
        /// Initializes and starts a dedicated audio rendering pipeline for a given slot.
        /// 
        /// Pipeline Steps:
        /// 1. Tears down any existing stream in this slot (KillStreamSlot).
        /// 2. Allocates a new BufferedWaveProvider with DiscardOnBufferOverflow=true to avoid unbounded queuing.
        /// 3. Injects calibrated silence padding (GetBasePaddingMs + delayMs) to ensure sub-millisecond sync with other outputs.
        /// 4. Wraps in SampleChannel for floating-point master/app volume scaling.
        /// 5. Automatically resamples via WdlResamplingSampleProvider if the device's native rate differs from 48 kHz.
        /// 6. Configures WASAPI render client with 150ms hardware buffer for Bluetooth (absorbs packet retries) or 50ms for wired.
        /// 7. Requests Windows Multimedia Class Scheduler Service (MMCSS) high-priority audio thread.
        /// </summary>
        private void StartSlotPlayback(int slotIndex, MMDevice dev)
        {
            if (capture == null || !isStreaming) return;

            try
            {
                KillStreamSlot(slotIndex);

                // 1. Allocate isolated ring buffer with overflow protection
                var newBuf = new BufferedWaveProvider(capture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true
                };

                int delayMs = slotIndex switch
                {
                    1 => delay1Ms,
                    2 => delay2Ms,
                    3 => delay3Ms,
                    _ => 0
                };

                bool isWireless = IsWirelessDevice(dev);
                switch (slotIndex)
                {
                    case 1: _isSlot1Wireless = isWireless; break;
                    case 2: _isSlot2Wireless = isWireless; break;
                    case 3: _isSlot3Wireless = isWireless; break;
                }

                // 2. Pre-fill exact calibrated silence padding for ZERO DRIFT sync
                int basePadding = GetBasePaddingMs(isWireless);
                int targetPaddingMs = basePadding + Math.Max(0, delayMs);
                int prefillBytes = (int)((long)targetPaddingMs * capture.WaveFormat.AverageBytesPerSecond / 1000);
                prefillBytes -= (prefillBytes % capture.WaveFormat.BlockAlign);
                if (prefillBytes > 0)
                {
                    int toAdd = Math.Min(prefillBytes, SilenceBuffer.Length);
                    newBuf.AddSamples(SilenceBuffer, 0, toAdd);
                }

                // 3. Floating point volume and software gain stage
                var sampleChannel = new SampleChannel(newBuf, true);
                double volVal = slotIndex switch
                {
                    1 => sliderVol1.Value,
                    2 => sliderVol2.Value,
                    3 => sliderVol3.Value,
                    _ => 100
                };
                float volFactor = (float)(volVal / 100.0);
                bool isHardwareBound = slotIndex switch
                {
                    1 => boundDevice1 != null,
                    2 => boundDevice2 != null,
                    3 => boundDevice3 != null,
                    _ => false
                };
                sampleChannel.Volume = cachedSysVol * (isHardwareBound ? 1.0f : volFactor);

                // 4. Sample rate conversion (e.g. 48kHz -> 44.1kHz or 96kHz if headset requires it)
                ISampleProvider prov = sampleChannel;
                int targetRate = GetDeviceMixFormat(dev).SampleRate;
                if (prov.WaveFormat.SampleRate != targetRate)
                {
                    prov = new WdlResamplingSampleProvider(prov, targetRate);
                }

                // 5. WASAPI player initialization with RF-safe latency buffer
                // Wireless devices benefit from 150ms WASAPI buffer to absorb packet retries without glitching
                int latency = isWireless ? 150 : 50;
                var player = new WasapiPlayerBuilder()
                    .WithDevice(dev)
                    .WithLatency(latency)
                    .WithMmcssThreadPriority(isWireless ? "Audio" : "Pro Audio")
                    .Build();

                player.Init(prov);

                lock (_bufferLock)
                {
                    switch (slotIndex)
                    {
                        case 1:
                            buffer1 = newBuf;
                            sampleChannel1 = sampleChannel;
                            out1 = player;
                            activeId1 = dev.ID;
                            _slot1Stalled = false;
                            _isSlot1UserPaused = false;
                            player.PlaybackStopped += (s, e) => { _slot1Stalled = true; };
                            break;
                        case 2:
                            buffer2 = newBuf;
                            sampleChannel2 = sampleChannel;
                            out2 = player;
                            activeId2 = dev.ID;
                            _slot2Stalled = false;
                            _isSlot2UserPaused = false;
                            player.PlaybackStopped += (s, e) => { _slot2Stalled = true; };
                            break;
                        case 3:
                            buffer3 = newBuf;
                            sampleChannel3 = sampleChannel;
                            out3 = player;
                            activeId3 = dev.ID;
                            _slot3Stalled = false;
                            _isSlot3UserPaused = false;
                            player.PlaybackStopped += (s, e) => { _slot3Stalled = true; };
                            break;
                    }
                }

                UpdateMaxBacklogLimits();
                UpdateSlotPauseButtonUI(slotIndex, false);
                player.Play();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StartSlotPlayback Error Slot {slotIndex}] {ex.Message}");
            }
        }

        private void KillStreamSlot(int slotIndex)
        {
            lock (_bufferLock)
            {
                switch (slotIndex)
                {
                    case 1:
                        _slot1Stalled = true;
                        try { out1?.Stop(); } catch { }
                        try { out1?.Dispose(); } catch { }
                        out1 = null;
                        buffer1 = null;
                        sampleChannel1 = null;
                        activeId1 = null;
                        break;
                    case 2:
                        _slot2Stalled = true;
                        try { out2?.Stop(); } catch { }
                        try { out2?.Dispose(); } catch { }
                        out2 = null;
                        buffer2 = null;
                        sampleChannel2 = null;
                        activeId2 = null;
                        break;
                    case 3:
                        _slot3Stalled = true;
                        try { out3?.Stop(); } catch { }
                        try { out3?.Dispose(); } catch { }
                        out3 = null;
                        buffer3 = null;
                        sampleChannel3 = null;
                        activeId3 = null;
                        break;
                }
            }
        }

        /// <summary>
        /// Recalculates maximum buffer capacity thresholds for each slot.
        /// 
        /// Why this is vital:
        /// When one Bluetooth headset moves far away or experiences 2.4GHz RF interference,
        /// Windows Bluetooth driver slows down its consumption rate.
        /// If we allowed buffers to grow indefinitely:
        /// 1. RAM usage would balloon.
        /// 2. Audio on the struggling headset would drift further and further behind.
        /// 3. The struggling device would starve the other headset of radio bandwidth.
        /// 
        /// By setting a hard ceiling at [Target Sync Padding + 800ms], we allow generous
        /// burst absorption for normal jitter, but if a headset falls further behind,
        /// packets are dropped ONLY for that struggling headset, leaving the other headsets completely unaffected!
        /// </summary>
        private void UpdateMaxBacklogLimits()
        {
            if (capture == null) return;
            int bps = capture.WaveFormat.AverageBytesPerSecond;

            // Generous 1500ms backlog ceiling to absorb Bluetooth retry bursts and dual-pairing handovers without packet drops
            int extraMs = 1500;
            int target1 = (int)((long)(GetBasePaddingMs(_isSlot1Wireless) + Math.Max(0, delay1Ms)) * bps / 1000);
            _maxBacklogBytes1 = target1 + (int)((long)extraMs * bps / 1000);

            int target2 = (int)((long)(GetBasePaddingMs(_isSlot2Wireless) + Math.Max(0, delay2Ms)) * bps / 1000);
            _maxBacklogBytes2 = target2 + (int)((long)extraMs * bps / 1000);

            int target3 = (int)((long)(GetBasePaddingMs(_isSlot3Wireless) + Math.Max(0, delay3Ms)) * bps / 1000);
            _maxBacklogBytes3 = target3 + (int)((long)extraMs * bps / 1000);
        }

        /// <summary>
        /// Handles system audio endpoint changes (e.g., Bluetooth reconnect, USB plug/unplug)
        /// received via Windows WM_DEVICECHANGE messages while streaming is active.
        /// </summary>
        private void HandleDeviceChangeWhileStreaming()
        {
            if (!isStreaming) return;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var activeEndpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                var activeMap = new Dictionary<string, MMDevice>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < activeEndpoints.Count; i++)
                {
                    var d = activeEndpoints[i];
                    activeMap[d.ID] = d;
                }

                // Check Slot 1 (Never auto-reconnect if user explicitly paused it)
                var dev1 = GetDeviceFromCombo(comboDevice1);
                if (dev1 != null)
                {
                    if (!_isSlot1UserPaused && (out1 == null || _slot1Stalled))
                    {
                        if (activeMap.TryGetValue(dev1.ID, out var aliveDev))
                        {
                            Debug.WriteLine($"[Auto-Reconnect] Slot 1 ({dev1.FriendlyName}) restored!");
                            StartSlotPlayback(1, aliveDev);
                            BindDevice1(aliveDev);
                        }
                    }
                    else if (activeId1 != null && !activeMap.ContainsKey(activeId1))
                    {
                        KillStreamSlot(1);
                    }
                }

                // Check Slot 2 (Never auto-reconnect if user explicitly paused it)
                var dev2 = GetDeviceFromCombo(comboDevice2);
                if (dev2 != null)
                {
                    if (!_isSlot2UserPaused && (out2 == null || _slot2Stalled))
                    {
                        if (activeMap.TryGetValue(dev2.ID, out var aliveDev))
                        {
                            Debug.WriteLine($"[Auto-Reconnect] Slot 2 ({dev2.FriendlyName}) restored!");
                            StartSlotPlayback(2, aliveDev);
                            BindDevice2(aliveDev);
                        }
                    }
                    else if (activeId2 != null && !activeMap.ContainsKey(activeId2))
                    {
                        KillStreamSlot(2);
                    }
                }

                // Check Slot 3 (Never auto-reconnect if user explicitly paused it)
                var dev3 = GetDeviceFromCombo(comboDevice3);
                if (dev3 != null)
                {
                    if (!_isSlot3UserPaused && (out3 == null || _slot3Stalled))
                    {
                        if (activeMap.TryGetValue(dev3.ID, out var aliveDev))
                        {
                            Debug.WriteLine($"[Auto-Reconnect] Slot 3 ({dev3.FriendlyName}) restored!");
                            StartSlotPlayback(3, aliveDev);
                            BindDevice3(aliveDev);
                        }
                    }
                    else if (activeId3 != null && !activeMap.ContainsKey(activeId3))
                    {
                        KillStreamSlot(3);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DeviceChange Handler Error] {ex.Message}");
            }
        }

        // --- Hardware Device Binding Methods ---

        private void UnbindAllDevices()
        {
            try { if (boundDevice1 != null) { boundDevice1.AudioEndpointVolume.OnVolumeNotification -= OnDevice1VolumeChanged; boundDevice1 = null; } } catch { boundDevice1 = null; }
            try { if (boundDevice2 != null) { boundDevice2.AudioEndpointVolume.OnVolumeNotification -= OnDevice2VolumeChanged; boundDevice2 = null; } } catch { boundDevice2 = null; }
            try { if (boundDevice3 != null) { boundDevice3.AudioEndpointVolume.OnVolumeNotification -= OnDevice3VolumeChanged; boundDevice3 = null; } } catch { boundDevice3 = null; }
        }

        private void BindDevice1(MMDevice? newDevice)
        {
            try { if (boundDevice1 != null) { boundDevice1.AudioEndpointVolume.OnVolumeNotification -= OnDevice1VolumeChanged; } } catch { }
            boundDevice1 = newDevice;
            if (boundDevice1 != null)
            {
                try
                {
                    _isSyncingVolume1 = true;
                    sliderVol1.Value = boundDevice1.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
                    lblVol1Val.Text = $"{(int)sliderVol1.Value}%";
                    _isSyncingVolume1 = false;
                    boundDevice1.AudioEndpointVolume.OnVolumeNotification += OnDevice1VolumeChanged;
                }
                catch { }
            }
            TryAutoLoadProfilesForActiveDevices();
            if (isVisualCalibrating) StartCalAudioForCurrentSlot();
        }

        private void BindDevice2(MMDevice? newDevice)
        {
            try { if (boundDevice2 != null) { boundDevice2.AudioEndpointVolume.OnVolumeNotification -= OnDevice2VolumeChanged; } } catch { }
            boundDevice2 = newDevice;
            if (boundDevice2 != null)
            {
                try
                {
                    _isSyncingVolume2 = true;
                    sliderVol2.Value = boundDevice2.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
                    lblVol2Val.Text = $"{(int)sliderVol2.Value}%";
                    _isSyncingVolume2 = false;
                    boundDevice2.AudioEndpointVolume.OnVolumeNotification += OnDevice2VolumeChanged;
                }
                catch { }
            }
            TryAutoLoadProfilesForActiveDevices();
            if (isVisualCalibrating) StartCalAudioForCurrentSlot();
        }

        private void BindDevice3(MMDevice? newDevice)
        {
            try { if (boundDevice3 != null) { boundDevice3.AudioEndpointVolume.OnVolumeNotification -= OnDevice3VolumeChanged; } } catch { }
            boundDevice3 = newDevice;
            if (boundDevice3 != null)
            {
                try
                {
                    _isSyncingVolume3 = true;
                    sliderVol3.Value = boundDevice3.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
                    lblVol3Val.Text = $"{(int)sliderVol3.Value}%";
                    _isSyncingVolume3 = false;
                    boundDevice3.AudioEndpointVolume.OnVolumeNotification += OnDevice3VolumeChanged;
                }
                catch { }
            }
            TryAutoLoadProfilesForActiveDevices();
            if (isVisualCalibrating) StartCalAudioForCurrentSlot();
        }

        private void OnDevice1VolumeChanged(AudioVolumeNotificationData data)
        {
            // Ignore echo notification if user recently dragged the slider (within 600ms)
            long elapsedMs = (Stopwatch.GetTimestamp() - _lastUserVol1ChangeTime) * 1000 / Stopwatch.Frequency;
            if (elapsedMs < 600) return;

            Dispatcher.UIThread.Post(() =>
            {
                double newVol = data.MasterVolume * 100;
                if (Math.Abs(sliderVol1.Value - newVol) > 1.0)
                {
                    _isSyncingVolume1 = true;
                    sliderVol1.Value = newVol;
                    lblVol1Val.Text = $"{(int)newVol}%";
                    if (sampleChannel1 != null)
                    {
                        sampleChannel1.Volume = cachedSysVol * (boundDevice1 != null ? 1.0f : (float)data.MasterVolume);
                    }
                    _isSyncingVolume1 = false;
                }
            });
        }

        private void OnDevice2VolumeChanged(AudioVolumeNotificationData data)
        {
            long elapsedMs = (Stopwatch.GetTimestamp() - _lastUserVol2ChangeTime) * 1000 / Stopwatch.Frequency;
            if (elapsedMs < 600) return;

            Dispatcher.UIThread.Post(() =>
            {
                double newVol = data.MasterVolume * 100;
                if (Math.Abs(sliderVol2.Value - newVol) > 1.0)
                {
                    _isSyncingVolume2 = true;
                    sliderVol2.Value = newVol;
                    lblVol2Val.Text = $"{(int)newVol}%";
                    if (sampleChannel2 != null)
                    {
                        sampleChannel2.Volume = cachedSysVol * (boundDevice2 != null ? 1.0f : (float)data.MasterVolume);
                    }
                    _isSyncingVolume2 = false;
                }
            });
        }

        private void OnDevice3VolumeChanged(AudioVolumeNotificationData data)
        {
            long elapsedMs = (Stopwatch.GetTimestamp() - _lastUserVol3ChangeTime) * 1000 / Stopwatch.Frequency;
            if (elapsedMs < 600) return;

            Dispatcher.UIThread.Post(() =>
            {
                double newVol = data.MasterVolume * 100;
                if (Math.Abs(sliderVol3.Value - newVol) > 1.0)
                {
                    _isSyncingVolume3 = true;
                    sliderVol3.Value = newVol;
                    lblVol3Val.Text = $"{(int)newVol}%";
                    if (sampleChannel3 != null)
                    {
                        sampleChannel3.Volume = cachedSysVol * (boundDevice3 != null ? 1.0f : (float)data.MasterVolume);
                    }
                    _isSyncingVolume3 = false;
                }
            });
        }

        private void ComboDevice1_SelectionChanged(object? sender, SelectionChangedEventArgs e) => BindDevice1(GetDeviceFromCombo(comboDevice1));
        private void ComboDevice2_SelectionChanged(object? sender, SelectionChangedEventArgs e) => BindDevice2(GetDeviceFromCombo(comboDevice2));
        private void ComboDevice3_SelectionChanged(object? sender, SelectionChangedEventArgs e) => BindDevice3(GetDeviceFromCombo(comboDevice3));

        private void LoadAudioDevices()
        {
            try
            {
                string? selectedName1 = GetPureDeviceName(comboDevice1.SelectedItem?.ToString());
                string? selectedName2 = GetPureDeviceName(comboDevice2.SelectedItem?.ToString());
                string? selectedName3 = GetPureDeviceName(comboDevice3.SelectedItem?.ToString());

                comboDevice1.SelectionChanged -= ComboDevice1_SelectionChanged;
                comboDevice2.SelectionChanged -= ComboDevice2_SelectionChanged;
                comboDevice3.SelectionChanged -= ComboDevice3_SelectionChanged;

                UnbindAllDevices();

                var items1 = new List<string> { "-- Disabled / Blank --" };
                var items2 = new List<string> { "-- Disabled / Blank --" };
                var items3 = new List<string> { "-- Disabled / Blank --" };

                foreach (var d in _cachedDevices)
                {
                    try { d.Dispose(); } catch { }
                }
                _cachedDevices.Clear();

                using var enumerator = new MMDeviceEnumerator();
                using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                for (int i = 0; i < endpoints.Count; i++)
                {
                    var dev = endpoints[i];
                    _cachedDevices.Add(dev);
                    string name = dev.FriendlyName;
                    string itemText = $"[{i}] {name}";
                    items1.Add(itemText);
                    items2.Add(itemText);
                    items3.Add(itemText);
                }

                comboDevice1.ItemsSource = items1;
                comboDevice2.ItemsSource = items2;
                comboDevice3.ItemsSource = items3;

                comboDevice1.SelectionChanged += ComboDevice1_SelectionChanged;
                comboDevice2.SelectionChanged += ComboDevice2_SelectionChanged;
                comboDevice3.SelectionChanged += ComboDevice3_SelectionChanged;

                SelectDeviceByName(comboDevice1, selectedName1, _cachedDevices, isInitialLoad && _cachedDevices.Count > 0 ? 1 : 0);
                SelectDeviceByName(comboDevice2, selectedName2, _cachedDevices, 0);
                SelectDeviceByName(comboDevice3, selectedName3, _cachedDevices, 0);

                isInitialLoad = false;
                TryAutoLoadProfilesForActiveDevices();
            }
            catch (Exception ex)
            {
                ShowError($"Error loading devices: {ex.Message}");
            }
        }

        private string? GetPureDeviceName(string? comboItemText)
        {
            if (string.IsNullOrEmpty(comboItemText) || comboItemText.StartsWith("--"))
                return null;

            int idx = comboItemText.IndexOf(']');
            if (idx >= 0 && idx + 1 < comboItemText.Length)
            {
                return comboItemText.Substring(idx + 1).TrimStart();
            }
            return comboItemText.Trim();
        }

        private void SelectDeviceByName(ComboBox cb, string? targetName, List<MMDevice> activeDevices, int defaultIdx)
        {
            if (targetName != null)
            {
                for (int i = 0; i < activeDevices.Count; i++)
                {
                    if (activeDevices[i].FriendlyName == targetName)
                    {
                        cb.SelectedIndex = i + 1;
                        return;
                    }
                }
            }

            if (cb.ItemsSource is IList<string> list && list.Count > defaultIdx)
                cb.SelectedIndex = defaultIdx;
            else
                cb.SelectedIndex = 0;
        }

        private static WaveFormat GetDeviceMixFormat(MMDevice dev)
        {
            using var client = dev.CreateAudioClient();
            return client.MixFormat;
        }

        private bool IsWirelessDevice(MMDevice device)
        {
            try
            {
                var instanceId = device.Properties.Contains(PropertyKeys.PKEY_Device_InstanceId)
                    ? device.Properties[PropertyKeys.PKEY_Device_InstanceId].Value?.ToString() ?? ""
                    : "";
                var controllerId = device.Properties.Contains(PropertyKeys.PKEY_Device_ControllerDeviceId)
                    ? device.Properties[PropertyKeys.PKEY_Device_ControllerDeviceId].Value?.ToString() ?? ""
                    : "";

                if (instanceId.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                    instanceId.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase) ||
                    controllerId.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                    controllerId.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch { }

            string name = device.FriendlyName;
            return name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Buds", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("WH-", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("WF-", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("QuietComfort", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Aeropex", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("OpenRun", StringComparison.OrdinalIgnoreCase);
        }

        private MMDevice? GetDeviceFromCombo(ComboBox cb)
        {
            if (cb.SelectedIndex <= 0 || (cb.SelectedIndex - 1) >= _cachedDevices.Count) return null;
            return _cachedDevices[cb.SelectedIndex - 1];
        }

        private async Task ExecuteBluetoothHardwareHandshake(MMDevice? dev1, MMDevice? dev2, MMDevice? dev3)
        {
            var devicesToWake = new[] { dev1, dev2, dev3 }.Where(d => d != null && IsWirelessDevice(d)).DistinctBy(d => d!.ID).ToList();
            if (devicesToWake.Count == 0) return;

            var players = new List<WasapiPlayer>();

            try
            {
                foreach (var dev in devicesToWake)
                {
                    if (dev == null) continue;
                    try
                    {
                        var mixFormat = GetDeviceMixFormat(dev);
                        var dummyProv = new BufferedWaveProvider(mixFormat) { DiscardOnBufferOverflow = true };

                        int bytesNeeded = mixFormat.AverageBytesPerSecond;
                        bytesNeeded -= bytesNeeded % mixFormat.BlockAlign;
                        int bytesToWrite = Math.Min(bytesNeeded, SilenceBuffer.Length);
                        dummyProv.AddSamples(SilenceBuffer, 0, bytesToWrite);

                        var player = new WasapiPlayerBuilder().WithDevice(dev).Build();
                        player.Init(dummyProv);
                        player.Play();
                        players.Add(player);
                    }
                    catch { }
                }

                if (players.Count > 0)
                {
                    await Task.Delay(1000);
                }
            }
            finally
            {
                foreach (var p in players)
                {
                    try { p.Stop(); } catch { }
                    try { p.Dispose(); } catch { }
                }
            }
        }

        #region Visual Calibration Studio (Ball Drop & Spacebar)

        internal class CalibrationClickProvider : ISampleProvider
        {
            private readonly WaveFormat _format;
            private readonly float[] _clickSamples;
            private int _position;
            private int _delayRemaining;
            private readonly int _sampleRate;
            private readonly int _channels;
            private readonly object _lock = new object();

            public CalibrationClickProvider(int sampleRate, int channels = 2)
            {
                _sampleRate = sampleRate;
                _channels = Math.Max(1, channels);
                _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
                int length = (int)(sampleRate * 0.045);
                _clickSamples = new float[length * _channels];
                for (int i = 0; i < length; i++)
                {
                    double t = (double)i / sampleRate;
                    double env = i < sampleRate * 0.002
                        ? (double)i / (sampleRate * 0.002)
                        : Math.Exp(-90.0 * (t - 0.002));
                    double val = (Math.Sin(2 * Math.PI * 1200 * t) * 0.7 + Math.Sin(2 * Math.PI * 2400 * t) * 0.3) * env * 0.95;
                    float sVal = (float)val;
                    for (int ch = 0; ch < _channels; ch++)
                    {
                        _clickSamples[i * _channels + ch] = sVal;
                    }
                }
                _position = _clickSamples.Length; // Silent initially
                _delayRemaining = 0;
            }

            public WaveFormat WaveFormat => _format;

            public void Trigger(int delayMs = 0)
            {
                lock (_lock)
                {
                    _delayRemaining = Math.Max(0, (int)((long)_sampleRate * delayMs / 1000) * _channels);
                    _position = 0;
                }
            }

            public int Read(Span<float> buffer)
            {
                buffer.Clear();
                lock (_lock)
                {
                    int offset = 0;
                    if (_delayRemaining > 0)
                    {
                        int delayChunk = Math.Min(buffer.Length, _delayRemaining);
                        _delayRemaining -= delayChunk;
                        offset = delayChunk;
                    }

                    if (offset < buffer.Length && _position < _clickSamples.Length)
                    {
                        int toCopy = Math.Min(buffer.Length - offset, _clickSamples.Length - _position);
                        _clickSamples.AsSpan(_position, toCopy).CopyTo(buffer.Slice(offset));
                        _position += toCopy;
                    }
                }
                return buffer.Length;
            }

            public int Read(float[] buffer, int offset, int count)
            {
                return Read(buffer.AsSpan(offset, count));
            }
        }

        // Master Global Calibration side panel trigger
        private void BtnGlobalCalibrate_Click(object? sender, RoutedEventArgs e) => OpenSidePanelForSlot(0);

        // Per-slot Pause / Resume button handlers
        private void BtnPauseSlot1_Click(object? sender, RoutedEventArgs e) => ToggleSlotPause(1);
        private void BtnPauseSlot2_Click(object? sender, RoutedEventArgs e) => ToggleSlotPause(2);
        private void BtnPauseSlot3_Click(object? sender, RoutedEventArgs e) => ToggleSlotPause(3);

        /// <summary>
        /// Toggles the streaming state of an individual slot between Paused and Active.
        /// When Paused: audio frames to this headset immediately cease, halting Bluetooth packet transmission
        /// and preventing ARQ packet retries from starving the other active headset during a phone call.
        /// When Resumed: residual audio is flushed and exact calibrated sync padding is injected, guaranteeing Zero Drift.
        /// </summary>
        private void ToggleSlotPause(int slotIndex)
        {
            if (!isStreaming) return;

            bool isCurrentlyPaused = slotIndex switch
            {
                1 => _isSlot1UserPaused,
                2 => _isSlot2UserPaused,
                3 => _isSlot3UserPaused,
                _ => false
            };

            if (isCurrentlyPaused)
            {
                // Resume slot with zero-drift calibrated alignment
                ResumeSlotWithCalibratedSync(slotIndex);
            }
            else
            {
                // Pause slot: immediately terminate hardware playback and release Bluetooth radio
                PauseSlot(slotIndex);
            }
        }

        /// <summary>
        /// Pauses the specified slot by completely shutting down its WASAPI render client.
        /// Stopping and disposing the audio client tells Windows to immediately close the A2DP stream,
        /// completely freeing 100% of the PC's single Bluetooth radio transceiver for the remaining active headset!
        /// </summary>
        private void PauseSlot(int slotIndex)
        {
            switch (slotIndex)
            {
                case 1:
                    _isSlot1UserPaused = true;
                    KillStreamSlot(1);
                    break;
                case 2:
                    _isSlot2UserPaused = true;
                    KillStreamSlot(2);
                    break;
                case 3:
                    _isSlot3UserPaused = true;
                    KillStreamSlot(3);
                    break;
            }

            UpdateSlotPauseButtonUI(slotIndex, true);
            Debug.WriteLine($"[User Action] Slot {slotIndex} PAUSED. Hardware stream closed & 100% Bluetooth radio airtime freed.");
        }

        /// <summary>
        /// Resumes streaming on a previously paused slot with ZERO AUDIO DRIFT.
        /// Resolves the live Windows CoreAudio endpoint (essential for Bluetooth headsets rejoining after a phone call),
        /// re-initializes a fresh WASAPI client, pre-fills exact calibrated silence padding,
        /// and guarantees instant acoustic sync without ever needing to restart the master stream.
        /// </summary>
        private void ResumeSlotWithCalibratedSync(int slotIndex)
        {
            if (capture == null || !isStreaming) return;

            ComboBox cb = slotIndex switch
            {
                1 => comboDevice1,
                2 => comboDevice2,
                3 => comboDevice3,
                _ => null!
            };
            if (cb == null) return;

            var dev = GetDeviceFromCombo(cb);
            if (dev == null)
            {
                Debug.WriteLine($"[User Action] Slot {slotIndex} cannot resume: device not configured.");
                return;
            }

            // Fresh resolution from Windows Audio Endpoint Enumerator:
            // When Bluetooth headsets rejoin after a phone call, stale cached COM pointers
            // can report inactive. Resolving live from MMDeviceEnumerator guarantees we get
            // the active, live device without needing to stop and restart the entire stream!
            MMDevice? liveDev = null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var candidate = enumerator.GetDevice(dev.ID);
                if (candidate != null && candidate.State == DeviceState.Active)
                {
                    liveDev = candidate;
                }
            }
            catch { }

            if (liveDev == null)
            {
                // Fallback: check all active endpoints by ID and FriendlyName in case Windows recreated the endpoint instance
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using var activeEndpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    for (int i = 0; i < activeEndpoints.Count; i++)
                    {
                        var ep = activeEndpoints[i];
                        if (string.Equals(ep.ID, dev.ID, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(ep.FriendlyName, dev.FriendlyName, StringComparison.OrdinalIgnoreCase))
                        {
                            liveDev = ep;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (liveDev == null || liveDev.State != DeviceState.Active)
            {
                Debug.WriteLine($"[User Action] Slot {slotIndex} cannot resume: {dev.FriendlyName} not active in Windows yet.");
                if (lblStatus != null)
                {
                    lblStatus.Text = $"Status: {dev.FriendlyName} is still reconnecting to Windows. Click Play once connected.";
                    lblStatus.Foreground = Brushes.Orange;
                }
                return;
            }

            switch (slotIndex)
            {
                case 1:
                    _isSlot1UserPaused = false;
                    BindDevice1(liveDev);
                    break;
                case 2:
                    _isSlot2UserPaused = false;
                    BindDevice2(liveDev);
                    break;
                case 3:
                    _isSlot3UserPaused = false;
                    BindDevice3(liveDev);
                    break;
            }

            // Fresh start ensures no corrupted WASAPI state or buffer underrun residue
            StartSlotPlayback(slotIndex, liveDev);
            Debug.WriteLine($"[User Action] Slot {slotIndex} RESUMED on reconnected device: {liveDev.FriendlyName} (Zero Drift).");
            if (lblStatus != null)
            {
                lblStatus.Text = "Status: Streaming live audio";
                lblStatus.Foreground = Brushes.LightGreen;
            }
        }

        private void UpdateSlotPauseButtonUI(int slotIndex, bool isPaused)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Button? btn = slotIndex switch
                {
                    1 => btnPauseSlot1,
                    2 => btnPauseSlot2,
                    3 => btnPauseSlot3,
                    _ => null
                };
                TextBlock? icon = slotIndex switch
                {
                    1 => iconPauseSlot1,
                    2 => iconPauseSlot2,
                    3 => iconPauseSlot3,
                    _ => null
                };

                if (btn != null && icon != null)
                {
                    if (isPaused)
                    {
                        btn.Background = PausedButtonBg;
                        btn.BorderBrush = PausedButtonBorder;
                        icon.Text = "▶";
                        ToolTip.SetTip(btn, $"Resume Slot {slotIndex} (Exact zero-drift sync alignment)");
                    }
                    else
                    {
                        btn.Background = ActiveButtonBg;
                        btn.BorderBrush = ActiveButtonBorder;
                        icon.Text = "⏸";
                        ToolTip.SetTip(btn, $"Pause Slot {slotIndex} (Frees Bluetooth radio for phone calls)");
                    }
                }
            });
        }

        private void OpenSidePanelForSlot(int slotIndex)
        {
            isVisualCalibrating = true;
            SelectCalibrationSlot(slotIndex);

            // If already open and not animating, simply refocus canvas
            if (!_isPanelAnimating && this.Width >= 919)
            {
                Dispatcher.UIThread.Post(() => canvasDropStage?.Focus(), DispatcherPriority.Loaded);
                return;
            }

            StartOpenPanelAnimation();
        }

        private void StartOpenPanelAnimation()
        {
            _isPanelAnimating = true;
            _panelAnimOpening = true;
            _isFirstAnimFrame = true;
            _animStartWidth = this.Width;
            _animTargetWidth = 920.0;

            this.MinWidth = 470.0;
            this.MaxWidth = 920.0;

            var trans = borderSidePanel.RenderTransform as TranslateTransform;
            if (trans != null) trans.X = 20.0;
            borderSidePanel.Opacity = 0.0;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null || Design.IsDesignMode)
            {
                this.Width = 920.0;
                this.MinWidth = 920.0;
                this.MaxWidth = 920.0;
                borderSidePanel.Opacity = 1.0;
                if (trans != null) trans.X = 0.0;
                _isPanelAnimating = false;
                Dispatcher.UIThread.Post(() => canvasDropStage?.Focus(), DispatcherPriority.Loaded);
                return;
            }

            topLevel.RequestAnimationFrame(OnPanelAnimationFrame);
        }

        private void BtnCloseSidePanel_Click(object? sender, RoutedEventArgs e)
        {
            CloseSidePanel();
        }

        private void CloseSidePanel(bool immediate = false)
        {
            _autoDropTimer?.Stop();
            _autoDropTimer = null;
            _spacebarDown = false;
            StopDrop();
            StopCalAudio();

            isVisualCalibrating = false;

            if (immediate || Design.IsDesignMode || TopLevel.GetTopLevel(this) == null)
            {
                _isPanelAnimating = false;
                borderSidePanel.Opacity = 0.0;
                var trans = borderSidePanel.RenderTransform as TranslateTransform;
                if (trans != null) trans.X = 20.0;
                this.MinWidth = 470.0;
                this.MaxWidth = 470.0;
                this.Width = 470.0;
                return;
            }

            _isPanelAnimating = true;
            _panelAnimOpening = false;
            _isFirstAnimFrame = true;
            _animStartWidth = this.Width;
            _animTargetWidth = 470.0;
            this.MinWidth = 470.0;

            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnPanelAnimationFrame);
        }

        private void OnPanelAnimationFrame(TimeSpan time)
        {
            if (!_isPanelAnimating) return;

            // Ensure the stopwatch starts on the exact first rendered frame (0ms elapsed),
            // so any layout preparation prior to frame 1 never steals duration from the animation.
            if (_isFirstAnimFrame)
            {
                _isFirstAnimFrame = false;
                _panelAnimStopwatch.Restart();
            }

            double elapsed = _panelAnimStopwatch.Elapsed.TotalMilliseconds;
            double progress = Math.Clamp(elapsed / PanelAnimDurationMs, 0.0, 1.0);
            var trans = borderSidePanel.RenderTransform as TranslateTransform;

            if (_panelAnimOpening)
            {
                // Quartic ease-out: 1 - (1 - progress)^4
                double inv = 1.0 - progress;
                double eased = 1.0 - (inv * inv * inv * inv);
                double currentWidth = _animStartWidth + (_animTargetWidth - _animStartWidth) * eased;

                this.Width = currentWidth;
                borderSidePanel.Opacity = eased;
                if (trans != null) trans.X = 20.0 * (1.0 - eased);

                if (progress >= 1.0)
                {
                    this.Width = 920.0;
                    this.MinWidth = 920.0;
                    this.MaxWidth = 920.0;
                    borderSidePanel.Opacity = 1.0;
                    if (trans != null) trans.X = 0.0;
                    _isPanelAnimating = false;
                    Dispatcher.UIThread.Post(() => canvasDropStage?.Focus(), DispatcherPriority.Loaded);
                }
                else
                {
                    TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnPanelAnimationFrame);
                }
            }
            else
            {
                // Quartic ease-in: progress^4
                double eased = progress * progress * progress * progress;
                double currentWidth = _animStartWidth - (_animStartWidth - _animTargetWidth) * eased;

                this.Width = Math.Max(470.0, currentWidth);
                borderSidePanel.Opacity = Math.Max(0.0, 1.0 - eased);
                if (trans != null) trans.X = 20.0 * eased;

                if (progress >= 1.0)
                {
                    this.Width = 470.0;
                    this.MinWidth = 470.0;
                    this.MaxWidth = 470.0;
                    borderSidePanel.Opacity = 0.0;
                    if (trans != null) trans.X = 20.0;
                    _isPanelAnimating = false;
                }
                else
                {
                    TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnPanelAnimationFrame);
                }
            }
        }

        private void BtnTabSlot_Click(object? sender, RoutedEventArgs e)
        {
            if (sender == btnTabGlobal) SelectCalibrationSlot(0);
            else if (sender == btnTabSlot1) SelectCalibrationSlot(1);
            else if (sender == btnTabSlot2) SelectCalibrationSlot(2);
            else if (sender == btnTabSlot3) SelectCalibrationSlot(3);
        }

        private void SelectCalibrationSlot(int slotIndex)
        {
            _autoDropTimer?.Stop();
            _autoDropTimer = null;
            StopDrop();

            _calActiveSlot = slotIndex;
            _recordedRawTaps.Clear();
            _nudgeOffsetMs = 0;
            if (lblFineTuneVal != null) lblFineTuneVal.Text = "0 ms";
            if (btnApplyCal != null)
            {
                btnApplyCal.IsEnabled = slotIndex == 0 ? globalOffsetMs > 0 : false;
                if (lblApplyCalText != null) lblApplyCalText.Text = "Apply";
            }

            if (btnTabGlobal != null) { btnTabGlobal.Background = slotIndex == 0 ? TabActiveBg : TabIdleBg; btnTabGlobal.Foreground = slotIndex == 0 ? HighlightBrush : TabIdleFg; }
            if (btnTabSlot1 != null) { btnTabSlot1.Background = slotIndex == 1 ? TabActiveBg : TabIdleBg; btnTabSlot1.Foreground = slotIndex == 1 ? HighlightBrush : TabIdleFg; }
            if (btnTabSlot2 != null) { btnTabSlot2.Background = slotIndex == 2 ? TabActiveBg : TabIdleBg; btnTabSlot2.Foreground = slotIndex == 2 ? HighlightBrush : TabIdleFg; }
            if (btnTabSlot3 != null) { btnTabSlot3.Background = slotIndex == 3 ? TabActiveBg : TabIdleBg; btnTabSlot3.Foreground = slotIndex == 3 ? HighlightBrush : TabIdleFg; }

            UpdateCalibrationDisplayForCurrentSlot();
            StartCalAudioForCurrentSlot();

            // Always return focus to the canvas stage so Space key events are routed correctly.
            Dispatcher.UIThread.Post(() => canvasDropStage?.Focus(), DispatcherPriority.Loaded);
        }

        private void UpdateCalibrationDisplayForCurrentSlot()
        {
            if (!isVisualCalibrating || borderSidePanel == null) return;

            // Reaction compensation & fine-tune card is active for both Global and Slot modes
            if (borderReactionTuneCard != null) borderReactionTuneCard.IsVisible = true;
            if (_recordedRawTaps.Count == 0 && borderFeedback != null) borderFeedback.IsVisible = false;
            if (lblTriggerDropText != null) lblTriggerDropText.Text = "Start Drop";
            if (lblApplyCalText != null) lblApplyCalText.Text = "Apply";
            if (lblClearSavedText != null) lblClearSavedText.Text = "Clear";

            if (_calActiveSlot == 0)
            {
                // Global section: show PC Speaker Mute & Auto Video Sync options, hide slot delay sliders
                if (borderGlobalSpeakerCard != null) borderGlobalSpeakerCard.IsVisible = true;
                if (borderGlobalSyncCard != null) borderGlobalSyncCard.IsVisible = true;
                if (borderSlotDelayCard != null)
                {
                    borderSlotDelayCard.IsVisible = true;
                    if (lblDelayCardHeader != null) lblDelayCardHeader.Text = "⏱ Output Delay Offsets";
                    _isUpdatingDelayText = true;
                    try
                    {
                        if (gridDelaySlot1 != null)
                        {
                            gridDelaySlot1.IsVisible = GetDeviceFromCombo(comboDevice1) != null;
                            if (txtDelay1 != null) txtDelay1.Text = delay1Ms.ToString();
                        }
                        if (gridDelaySlot2 != null)
                        {
                            gridDelaySlot2.IsVisible = GetDeviceFromCombo(comboDevice2) != null;
                            if (txtDelay2 != null) txtDelay2.Text = delay2Ms.ToString();
                        }
                        if (gridDelaySlot3 != null)
                        {
                            gridDelaySlot3.IsVisible = GetDeviceFromCombo(comboDevice3) != null;
                            if (txtDelay3 != null) txtDelay3.Text = delay3Ms.ToString();
                        }
                    }
                    finally
                    {
                        _isUpdatingDelayText = false;
                    }
                }

                if (lblCalModeHeader != null) lblCalModeHeader.Text = "Target: ";
                if (lblCalActiveDevice != null)
                {
                    var names = new[] { GetDeviceFromCombo(comboDevice1)?.FriendlyName, GetDeviceFromCombo(comboDevice2)?.FriendlyName, GetDeviceFromCombo(comboDevice3)?.FriendlyName }
                        .Where(n => !string.IsNullOrEmpty(n)).ToList();
                    lblCalActiveDevice.Text = names.Count > 0 ? $"🌐 All: {string.Join(" + ", names)}" : "🌐 No active devices selected";
                    lblCalActiveDevice.Foreground = Brushes.White;
                }
                if (lblStagePrompt != null)
                {
                    lblStagePrompt.Text = "Click 'Start Drop' or press [Spacebar] to calibrate all devices!";
                    lblStagePrompt.Foreground = Brushes.White;
                }
                if (btnApplyCal != null)
                {
                    btnApplyCal.IsEnabled = (_recordedRawTaps.Count > 0 || globalOffsetMs > 0);
                }
                if (btnClearSaved != null)
                {
                    ToolTip.SetTip(btnClearSaved, "Clear all saved calibration profiles and reset delays to 0 ms");
                }

                if (_recordedRawTaps.Count > 0)
                {
                    UpdateTapDisplay();
                }
                else
                {
                    if (globalOffsetMs > 0)
                    {
                        if (lblAvgLatency != null) lblAvgLatency.Text = $"{globalOffsetMs} ms";
                        if (lblTapHistory != null) lblTapHistory.Text = $"Current global sync: {globalOffsetMs} ms";
                    }
                    else
                    {
                        if (lblAvgLatency != null) lblAvgLatency.Text = "-- ms";
                        if (lblTapHistory != null) lblTapHistory.Text = "Press [Spacebar] when sound is heard";
                    }
                }
                return;
            }

            // Hide global PC speaker card & global sync card in individual slot mode
            if (borderGlobalSpeakerCard != null) borderGlobalSpeakerCard.IsVisible = false;
            if (borderGlobalSyncCard != null) borderGlobalSyncCard.IsVisible = false;

            if (lblCalModeHeader != null) lblCalModeHeader.Text = $"Slot {_calActiveSlot}: ";
            if (btnClearSaved != null) ToolTip.SetTip(btnClearSaved, $"Clear saved calibration profile for Slot {_calActiveSlot} and reset delay to 0 ms");

            // Show slot delay card with only the corresponding slot slider
            if (borderSlotDelayCard != null)
            {
                borderSlotDelayCard.IsVisible = true;
                if (lblDelayCardHeader != null) lblDelayCardHeader.Text = $"⏱ Slot {_calActiveSlot} Delay Offset";
                _isUpdatingDelayText = true;
                try
                {
                    if (gridDelaySlot1 != null)
                    {
                        gridDelaySlot1.IsVisible = _calActiveSlot == 1;
                        if (_calActiveSlot == 1 && txtDelay1 != null) txtDelay1.Text = delay1Ms.ToString();
                    }
                    if (gridDelaySlot2 != null)
                    {
                        gridDelaySlot2.IsVisible = _calActiveSlot == 2;
                        if (_calActiveSlot == 2 && txtDelay2 != null) txtDelay2.Text = delay2Ms.ToString();
                    }
                    if (gridDelaySlot3 != null)
                    {
                        gridDelaySlot3.IsVisible = _calActiveSlot == 3;
                        if (_calActiveSlot == 3 && txtDelay3 != null) txtDelay3.Text = delay3Ms.ToString();
                    }
                }
                finally
                {
                    _isUpdatingDelayText = false;
                }
            }

            var dev = GetActiveCalDevice();
            if (dev != null)
            {
                if (lblCalActiveDevice != null)
                {
                    lblCalActiveDevice.Text = dev.FriendlyName;
                    lblCalActiveDevice.Foreground = Brushes.White;
                }
                if (lblStagePrompt != null)
                {
                    lblStagePrompt.Text = "Click 'Start Drop' or press [Spacebar] to drop!";
                    lblStagePrompt.Foreground = Brushes.White;
                }

                int? savedLat = GetSavedDeviceDelay(dev.FriendlyName) ?? GetSavedProfileLatency(dev.FriendlyName);
                if (savedLat.HasValue)
                {
                    if (lblAvgLatency != null) lblAvgLatency.Text = $"{savedLat.Value} ms";
                    if (lblTapHistory != null) lblTapHistory.Text = $"Loaded from saved profile: {savedLat.Value} ms";
                    if (btnApplyCal != null) btnApplyCal.IsEnabled = true;
                }
                else
                {
                    if (lblAvgLatency != null) lblAvgLatency.Text = "-- ms";
                    if (lblTapHistory != null) lblTapHistory.Text = "Not yet calibrated. Press [Spacebar] when sound is heard.";
                    if (btnApplyCal != null) btnApplyCal.IsEnabled = false;
                }
            }
            else
            {
                if (lblCalActiveDevice != null)
                {
                    lblCalActiveDevice.Text = "[No Device Selected]";
                    lblCalActiveDevice.Foreground = Brushes.Orange;
                }
                if (lblStagePrompt != null)
                {
                    lblStagePrompt.Text = $"Please select an output device for Slot {_calActiveSlot} first.";
                    lblStagePrompt.Foreground = Brushes.Orange;
                }
                if (lblAvgLatency != null) lblAvgLatency.Text = "-- ms";
                if (lblTapHistory != null) lblTapHistory.Text = "Select device in the left dropdown menu.";
                if (btnApplyCal != null) btnApplyCal.IsEnabled = false;
            }
        }

        private MMDevice? GetActiveCalDevice()
        {
            return _calActiveSlot switch
            {
                1 => GetDeviceFromCombo(comboDevice1),
                2 => GetDeviceFromCombo(comboDevice2),
                3 => GetDeviceFromCombo(comboDevice3),
                _ => null
            };
        }

        private void StartCalAudioForCurrentSlot()
        {
            _ = StartCalAudioForCurrentSlotAsync();
        }

        private async Task StartCalAudioForCurrentSlotAsync()
        {
            int slot = _calActiveSlot;
            MMDevice? dev1 = GetDeviceFromCombo(comboDevice1);
            MMDevice? dev2 = GetDeviceFromCombo(comboDevice2);
            MMDevice? dev3 = GetDeviceFromCombo(comboDevice3);
            MMDevice? singleDev = GetActiveCalDevice();

            await Task.Run(() =>
            {
                StopCalAudio();

                if (slot == 0)
                {
                    var activeDevs = new (MMDevice? dev, int slot)[]
                    {
                        (dev1, 1),
                        (dev2, 2),
                        (dev3, 3)
                    };

                    foreach (var (dev, slotIndex) in activeDevs)
                    {
                        if (dev == null) continue;
                        try
                        {
                            bool isWireless = IsWirelessDevice(dev);
                            int latency = isWireless ? 100 : 50;
                            string mmcss = isWireless ? "Audio" : "Pro Audio";

                            var mixFormat = GetDeviceMixFormat(dev);
                            var provider = new CalibrationClickProvider(mixFormat.SampleRate, mixFormat.Channels);
                            var player = new WasapiPlayerBuilder()
                                .WithDevice(dev)
                                .WithLatency(latency)
                                .WithMmcssThreadPriority(mmcss)
                                .Build();

                            player.Init(provider);
                            player.Play();
                            lock (_globalCalOutputs)
                            {
                                _globalCalOutputs.Add((player, provider, slotIndex, isWireless));
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[GlobalCalAudio Error] {ex.Message}");
                        }
                    }
                    return;
                }

                if (singleDev == null) return;
                try
                {
                    bool isWireless = IsWirelessDevice(singleDev);
                    int latency = isWireless ? 100 : 50;
                    string mmcss = isWireless ? "Audio" : "Pro Audio";

                    var mixFormat = GetDeviceMixFormat(singleDev);
                    var provider = new CalibrationClickProvider(mixFormat.SampleRate, mixFormat.Channels);
                    var player = new WasapiPlayerBuilder()
                        .WithDevice(singleDev)
                        .WithLatency(latency)
                        .WithMmcssThreadPriority(mmcss)
                        .Build();

                    player.Init(provider);
                    player.Play();
                    _calClickProvider = provider;
                    _calPlayer = player;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CalAudio Error] {ex.Message}");
                }
            });
        }

        private void StopCalAudio()
        {
            lock (_globalCalOutputs)
            {
                foreach (var (player, _, _, _) in _globalCalOutputs)
                {
                    try { player.Stop(); } catch { }
                    try { player.Dispose(); } catch { }
                }
                _globalCalOutputs.Clear();
            }

            try { _calPlayer?.Stop(); } catch { }
            try { _calPlayer?.Dispose(); } catch { }
            _calPlayer = null;
            _calClickProvider = null;
        }

        private void BtnTriggerDrop_Click(object? sender, RoutedEventArgs e)
        {
            // Always cancel any pending auto-drop timer when user manually clicks
            _autoDropTimer?.Stop();
            _autoDropTimer = null;

            // If a previous calibration completed (3+ taps recorded), reset for a fresh run
            if (_recordedRawTaps.Count >= 3)
            {
                _recordedRawTaps.Clear();
                _nudgeOffsetMs = 0;
                if (lblFineTuneVal != null) lblFineTuneVal.Text = "0 ms";
                UpdateTapDisplay();
            }
            StartDrop();
        }

        private async void StartDrop()
        {
            if (_isDropping) return;

            if (_calActiveSlot == 0)
            {
                var d1 = GetDeviceFromCombo(comboDevice1);
                var d2 = GetDeviceFromCombo(comboDevice2);
                var d3 = GetDeviceFromCombo(comboDevice3);
                if (d1 == null && d2 == null && d3 == null)
                {
                    ShowWarning("Please select at least one active output device first.");
                    return;
                }
                if (_globalCalOutputs.Count == 0)
                {
                    await StartCalAudioForCurrentSlotAsync();
                }
            }
            else
            {
                var dev = GetActiveCalDevice();
                if (dev == null)
                {
                    ShowWarning($"Please select an active device for Slot {_calActiveSlot} first.");
                    return;
                }

                if (_calPlayer == null)
                {
                    await StartCalAudioForCurrentSlotAsync();
                }
            }

            _isDropping = true;
            _hasImpacted = false;
            _awaitingTap = false;
            _rippleActive = false;

            lblStagePrompt.Text = "Dropping... Watch the ball & listen!";
            lblStagePrompt.Foreground = HighlightBrush;
            borderFeedback.IsVisible = false;

            Canvas.SetTop(ballElement, StartY);
            rippleRing.Opacity = 0;

            _dropStopwatch.Restart();
            var topLevel = TopLevel.GetTopLevel(this);
            topLevel?.RequestAnimationFrame(OnAnimationFrame);
        }

        private void OnAnimationFrame(TimeSpan time)
        {
            if (!_isDropping) return;

            double elapsed = _dropStopwatch.Elapsed.TotalMilliseconds;
            double progress = Math.Clamp(elapsed / DropDurationMs, 0.0, 1.0);

            // Gravity acceleration curve (Ease-In: progress^1.8)
            double eased = Math.Pow(progress, 1.8);
            double targetTop = TargetY - BallDiameter;
            double currentTop = StartY + (targetTop - StartY) * eased;
            Canvas.SetTop(ballElement, currentTop);

            if (elapsed >= DropDurationMs && !_hasImpacted)
            {
                _hasImpacted = true;
                OnBallImpact();
            }

            if (_rippleActive)
            {
                double rippleElapsed = (double)(Stopwatch.GetTimestamp() - _rippleStartTimestamp) * 1000.0 / Stopwatch.Frequency;
                double rippleProgress = Math.Clamp(rippleElapsed / 260.0, 0.0, 1.0);
                double size = 4.0 + (55.0 - 4.0) * rippleProgress;
                rippleRing.Width = size;
                rippleRing.Height = size;
                Canvas.SetLeft(rippleRing, 210.0 - size / 2.0);
                Canvas.SetTop(rippleRing, TargetY - size / 2.0);
                rippleRing.Opacity = 1.0 - rippleProgress;
                if (rippleProgress >= 1.0)
                {
                    _rippleActive = false;
                    rippleRing.Opacity = 0;
                }
            }

            // After 2.5 seconds without tap, time out this drop
            if (elapsed >= DropDurationMs + 2500.0)
            {
                StopDrop();
                if (_awaitingTap)
                {
                    _awaitingTap = false;
                    lblStagePrompt.Text = "Missed tap! Click 'Start Drop' or press [Spacebar] to try again.";
                    lblStagePrompt.Foreground = Brushes.Orange;
                }
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            topLevel?.RequestAnimationFrame(OnAnimationFrame);
        }

        private void OnBallImpact()
        {
            _impactTimestamp = Stopwatch.GetTimestamp();
            _awaitingTap = true;

            if (_calActiveSlot == 0)
            {
                // Global mode: play transient to all active outputs with their configured delay offsets and base padding
                foreach (var (_, provider, slot, isWireless) in _globalCalOutputs)
                {
                    int delay = slot switch
                    {
                        1 => delay1Ms,
                        2 => delay2Ms,
                        3 => delay3Ms,
                        _ => 0
                    };
                    int basePadding = GetBasePaddingMs(isWireless);
                    provider.Trigger(basePadding + delay);
                }
            }
            else
            {
                var dev = GetActiveCalDevice();
                bool isWireless = dev != null && IsWirelessDevice(dev);
                int basePadding = GetBasePaddingMs(isWireless);
                _calClickProvider?.Trigger(basePadding);
            }

            TriggerImpactRipple();
            lblStagePrompt.Text = "🔊 SOUND PLAYED! Press [SPACEBAR] NOW!";
            lblStagePrompt.Foreground = Brushes.Orange;
        }

        private void TriggerImpactRipple()
        {
            rippleRing.Width = 4;
            rippleRing.Height = 4;
            Canvas.SetLeft(rippleRing, 210 - 2);
            Canvas.SetTop(rippleRing, TargetY - 2);
            rippleRing.Opacity = 1.0;
            _rippleStartTimestamp = Stopwatch.GetTimestamp();
            _rippleActive = true;
        }

        private void StopDrop()
        {
            _autoDropTimer?.Stop();
            _autoDropTimer = null;
            _isDropping = false;
            _hasImpacted = false;
            _awaitingTap = false;
            _rippleActive = false;
            Canvas.SetTop(ballElement, StartY);
        }

        private void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (comboDevice1.IsDropDownOpen || comboDevice2.IsDropDownOpen || comboDevice3.IsDropDownOpen) return;
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;

            if (isVisualCalibrating && this.Width >= 800 && e.Key == Key.Space)
            {
                e.Handled = true; // Prevent activating any focused UI button

                // Suppress OS key-repeat: only act on the first physical key-down event
                if (_spacebarDown) return;
                _spacebarDown = true;

                HandleSpacebarTap();
            }
        }

        // Belt-and-suspenders: OnKeyDown fires even when the Window itself holds focus
        // (no child element focused), which happens when user clicks in the non-focusable side panel.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            {
                base.OnKeyDown(e);
                return;
            }

            if (isVisualCalibrating && this.Width >= 800 && e.Key == Key.Space)
            {
                e.Handled = true;
                if (!_spacebarDown)
                {
                    _spacebarDown = true;
                    HandleSpacebarTap();
                }
                return; // Don't call base — prevents any residual button activation
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.Key == Key.Space)
                _spacebarDown = false;
            base.OnKeyUp(e);
        }

        private void HandleSpacebarTap()
        {
            if (!_isDropping)
            {
                // Always cancel any pending auto-drop timer on manual spacebar press
                _autoDropTimer?.Stop();
                _autoDropTimer = null;

                // If previous calibration completed (3 taps), reset before starting new run
                if (_recordedRawTaps.Count >= 3)
                {
                    _recordedRawTaps.Clear();
                    _nudgeOffsetMs = 0;
                    if (lblFineTuneVal != null) lblFineTuneVal.Text = "0 ms";
                    UpdateTapDisplay();
                }
                StartDrop();
                return;
            }

            if (!_hasImpacted)
            {
                lblStagePrompt.Text = "⚠️ Too early! Wait until you HEAR the sound.";
                lblStagePrompt.Foreground = Brushes.Orange;
                borderFeedback.IsVisible = true;
                lblFeedbackText.Text = "TOO EARLY!";
                lblFeedbackText.Foreground = Brushes.Orange;
                StopDrop();
                return;
            }

            if (_awaitingTap)
            {
                _awaitingTap = false;
                long pressTimestamp = Stopwatch.GetTimestamp();
                double rawDeltaMs = (double)(pressTimestamp - _impactTimestamp) * 1000.0 / Stopwatch.Frequency;
                StopDrop();

                if (rawDeltaMs < 30 || rawDeltaMs > 2500)
                {
                    lblStagePrompt.Text = "Tap out of range. Please try again.";
                    lblStagePrompt.Foreground = Brushes.Orange;
                    return;
                }

                _recordedRawTaps.Add(rawDeltaMs);
                UpdateTapDisplay();

                int currentCalc = CalculateEffectiveLatency();
                borderFeedback.IsVisible = true;
                lblFeedbackText.Text = $"Tap: {rawDeltaMs:F0} ms";
                lblFeedbackText.Foreground = HighlightBrush;

                if (_recordedRawTaps.Count < 3)
                {
                    lblStagePrompt.Text = $"Tap {_recordedRawTaps.Count} recorded! Next drop in 1s...";
                    lblStagePrompt.Foreground = Brushes.LightGreen;

                    _autoDropTimer?.Stop();
                    _autoDropTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
                    _autoDropTimer.Tick += (s, args) =>
                    {
                        _autoDropTimer?.Stop();
                        _autoDropTimer = null;
                        if (isVisualCalibrating && _recordedRawTaps.Count > 0 && _recordedRawTaps.Count < 3)
                        {
                            StartDrop();
                        }
                    };
                    _autoDropTimer.Start();
                }
                else
                {
                    lblStagePrompt.Text = $"Calibration complete! Estimated latency: {currentCalc} ms. Click 'Apply'.";
                    lblStagePrompt.Foreground = Brushes.LightGreen;
                }
            }
        }

        private int CalculateEffectiveLatency()
        {
            if (_recordedRawTaps.Count == 0)
            {
                if (_calActiveSlot == 0)
                {
                    return Math.Max(0, globalOffsetMs + _nudgeOffsetMs);
                }
                var dev = GetActiveCalDevice();
                int? saved = dev != null ? GetSavedProfileLatency(dev.FriendlyName) : null;
                if (saved.HasValue) return Math.Max(0, saved.Value + _nudgeOffsetMs);
                return 0;
            }

            double avgRaw = _recordedRawTaps.Average();

            double result;
            if (chkHumanReaction?.IsChecked == true)
            {
                // Compensate 200ms human reaction time
                result = Math.Max(0, avgRaw - 200.0);
            }
            else
            {
                result = avgRaw;
            }

            result += _nudgeOffsetMs;
            return Math.Max(0, (int)(Math.Round(result / 5.0) * 5));
        }

        private void UpdateTapDisplay()
        {
            if (lblTapHistory == null || lblAvgLatency == null || btnApplyCal == null) return;

            if (_recordedRawTaps.Count == 0)
            {
                if (_calActiveSlot == 0)
                {
                    if (globalOffsetMs > 0)
                    {
                        lblTapHistory.Text = $"Current global sync: {globalOffsetMs} ms";
                        lblAvgLatency.Text = $"{globalOffsetMs + _nudgeOffsetMs} ms";
                        btnApplyCal.IsEnabled = true;
                    }
                    else
                    {
                        lblTapHistory.Text = "Press [Spacebar] when sound is heard";
                        lblAvgLatency.Text = "-- ms";
                        btnApplyCal.IsEnabled = false;
                    }
                    if (lblApplyCalText != null) lblApplyCalText.Text = "Apply";
                    return;
                }

                var dev = GetActiveCalDevice();
                int? saved = dev != null ? GetSavedProfileLatency(dev.FriendlyName) : null;
                if (saved.HasValue)
                {
                    lblTapHistory.Text = $"Loaded from saved profile: {saved.Value} ms";
                    lblAvgLatency.Text = $"{saved.Value + _nudgeOffsetMs} ms";
                    btnApplyCal.IsEnabled = true;
                }
                else
                {
                    lblTapHistory.Text = "Press [Spacebar] when sound is heard";
                    lblAvgLatency.Text = "-- ms";
                    btnApplyCal.IsEnabled = false;
                }
                if (lblApplyCalText != null) lblApplyCalText.Text = "Apply";
                return;
            }

            lblTapHistory.Text = string.Join(" | ", _recordedRawTaps.Select((t, i) => $"#{i + 1}: {t:F0}ms"));
            int eff = CalculateEffectiveLatency();
            lblAvgLatency.Text = $"{eff} ms";
            btnApplyCal.IsEnabled = true;
            if (lblApplyCalText != null) lblApplyCalText.Text = "Apply";
        }

        private void ChkHumanReaction_Changed(object? sender, RoutedEventArgs e)
        {
            if (lblTapHistory == null || lblAvgLatency == null || btnApplyCal == null) return;
            UpdateTapDisplay();
        }

        private void BtnResetTaps_Click(object? sender, RoutedEventArgs e)
        {
            _autoDropTimer?.Stop();
            _autoDropTimer = null;
            _recordedRawTaps.Clear();
            _nudgeOffsetMs = 0;
            if (lblFineTuneVal != null) lblFineTuneVal.Text = "0 ms";
            if (borderFeedback != null) borderFeedback.IsVisible = false;
            if (lblStagePrompt != null)
            {
                lblStagePrompt.Text = "Taps reset. Click 'Start Drop' or press [Spacebar]!";
                lblStagePrompt.Foreground = Brushes.White;
            }
            UpdateTapDisplay();
        }

        private void BtnNudgeDown_Click(object? sender, RoutedEventArgs e)
        {
            _nudgeOffsetMs -= 5;
            if (lblFineTuneVal != null) lblFineTuneVal.Text = $"{_nudgeOffsetMs:+#;-#;0} ms";
            UpdateTapDisplay();
        }

        private void BtnNudgeUp_Click(object? sender, RoutedEventArgs e)
        {
            _nudgeOffsetMs += 5;
            if (lblFineTuneVal != null) lblFineTuneVal.Text = $"{_nudgeOffsetMs:+#;-#;0} ms";
            UpdateTapDisplay();
        }

        private async void BtnApplyCal_Click(object? sender, RoutedEventArgs e)
        {
            if (_calActiveSlot == 0)
            {
                var d1 = GetDeviceFromCombo(comboDevice1);
                var d2 = GetDeviceFromCombo(comboDevice2);
                var d3 = GetDeviceFromCombo(comboDevice3);

                if (d1 == null && d2 == null && d3 == null)
                {
                    ShowWarning("Please select at least one active output device first.");
                    return;
                }

                int finalLatency = CalculateEffectiveLatency();
                if (finalLatency <= 0 && _recordedRawTaps.Count == 0 && globalOffsetMs > 0)
                {
                    finalLatency = globalOffsetMs;
                }

                // 1. Set global Media Lip-Sync Offset
                UpdateGlobalOffset(finalLatency);

                // 2. Calibrate and save all active devices to this latency
                if (d1 != null) SaveDeviceProfile(d1.FriendlyName, finalLatency);
                if (d2 != null) SaveDeviceProfile(d2.FriendlyName, finalLatency);
                if (d3 != null) SaveDeviceProfile(d3.FriendlyName, finalLatency);

                // 3. Reset delays to 0 since all devices calibrated together in Global sync
                SetSlotDelay(1, 0, applyIfStreaming: false);
                SetSlotDelay(2, 0, applyIfStreaming: false);
                SetSlotDelay(3, 0, applyIfStreaming: false);

                // 4. Update audio delays in real-time if streaming
                if (isStreaming)
                {
                    ApplyDelays();
                }

                lblStatus.Text = $"Status: Global calibration applied & saved for all devices at {finalLatency} ms!";
                lblStatus.Foreground = Brushes.LightGreen;

                if (lblApplyCalText != null) lblApplyCalText.Text = "Saved! ✓";
                await Task.Delay(1500);
                if (lblApplyCalText != null) lblApplyCalText.Text = "Apply";
                return;
            }

            var dev = GetActiveCalDevice();
            if (dev == null) return;

            int slotLatency = CalculateEffectiveLatency();
            SaveDeviceProfile(dev.FriendlyName, slotLatency);

            // Automatically recalculate multi-device delays and lip-sync offset
            TryAutoLoadProfilesForActiveDevices(forceEvenIfStreaming: true);
            if (isStreaming)
            {
                ApplyDelays();
            }

            lblStatus.Text = $"Status: Slot {_calActiveSlot} ({dev.FriendlyName}) calibrated & saved at {slotLatency} ms!";
            lblStatus.Foreground = Brushes.LightGreen;

            if (lblApplyCalText != null) lblApplyCalText.Text = "Saved! ✓";
            await Task.Delay(1500);
            if (lblApplyCalText != null) lblApplyCalText.Text = "Apply";
        }

        private async void BtnClearSaved_Click(object? sender, RoutedEventArgs e)
        {
            if (_calActiveSlot == 0)
            {
                var d1 = GetDeviceFromCombo(comboDevice1);
                var d2 = GetDeviceFromCombo(comboDevice2);
                var d3 = GetDeviceFromCombo(comboDevice3);

                if (d1 != null) ClearDeviceProfile(d1.FriendlyName);
                if (d2 != null) ClearDeviceProfile(d2.FriendlyName);
                if (d3 != null) ClearDeviceProfile(d3.FriendlyName);

                SetSlotDelay(1, 0, applyIfStreaming: false);
                SetSlotDelay(2, 0, applyIfStreaming: false);
                SetSlotDelay(3, 0, applyIfStreaming: false);
                UpdateGlobalOffset(0);

                if (isStreaming)
                {
                    ApplyDelays();
                }

                _recordedRawTaps.Clear();
                _nudgeOffsetMs = 0;
                if (lblFineTuneVal != null) lblFineTuneVal.Text = "0 ms";

                UpdateCalibrationDisplayForCurrentSlot();

                lblStatus.Text = "Status: Cleared all saved profiles & reset delays to 0 ms.";
                lblStatus.Foreground = Brushes.LightGreen;
            }
            else
            {
                var dev = GetActiveCalDevice();
                if (dev != null)
                {
                    ClearDeviceProfile(dev.FriendlyName);

                    SetSlotDelay(_calActiveSlot, 0, applyIfStreaming: false);

                    _recordedRawTaps.Clear();
                    _nudgeOffsetMs = 0;
                    if (lblFineTuneVal != null) lblFineTuneVal.Text = "0 ms";

                    TryAutoLoadProfilesForActiveDevices(forceEvenIfStreaming: true);
                    if (isStreaming)
                    {
                        ApplyDelays();
                    }
                    UpdateCalibrationDisplayForCurrentSlot();

                    lblStatus.Text = $"Status: Cleared saved calibration profile for Slot {_calActiveSlot}.";
                    lblStatus.Foreground = Brushes.LightGreen;
                }
                else
                {
                    SetSlotDelay(_calActiveSlot, 0, applyIfStreaming: false);

                    _recordedRawTaps.Clear();
                    _nudgeOffsetMs = 0;
                    if (lblFineTuneVal != null) lblFineTuneVal.Text = "0 ms";
                    UpdateCalibrationDisplayForCurrentSlot();
                }
            }

            if (lblClearSavedText != null)
            {
                lblClearSavedText.Text = "Cleared! ✓";
                await Task.Delay(1500);
                if (lblClearSavedText != null)
                {
                    lblClearSavedText.Text = "Clear";
                }
            }
        }

        #endregion

        #region Auto Audio-Video Sync (Milestone 2)

        private void UpdateGlobalOffset(int newOffset)
        {
            globalOffsetMs = Math.Max(0, newOffset);
            if (lblGlobalOffset != null)
            {
                lblGlobalOffset.Text = globalOffsetMs > 0 ? $"-{globalOffsetMs} ms" : "0 ms";
            }
            _browserSyncServer.CurrentOffsetMs = globalOffsetMs;
            if (isStreaming && globalOffsetMs > 0 && _mediaPlayerSyncService.IsAutoSyncEnabled)
            {
                _ = _mediaPlayerSyncService.TrySyncActivePlayersAsync(globalOffsetMs);
            }
        }

        private async void BtnAutoSyncPlayer_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (btnAutoSyncPlayer != null)
                {
                    btnAutoSyncPlayer.IsEnabled = false;
                    btnAutoSyncPlayer.Content = "Syncing...";
                }

                string result = await _mediaPlayerSyncService.TrySyncActivePlayersAsync(globalOffsetMs, force: true);
                if (lblPlayerSyncStatus != null)
                {
                    lblPlayerSyncStatus.Text = result;
                    lblPlayerSyncStatus.Foreground = result.StartsWith("🟢")
                        ? Brushes.LightGreen
                        : new SolidColorBrush(Color.FromRgb(160, 174, 192));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BtnAutoSyncPlayer Error] {ex.Message}");
            }
            finally
            {
                await Task.Delay(500);
                if (btnAutoSyncPlayer != null)
                {
                    btnAutoSyncPlayer.IsEnabled = true;
                    btnAutoSyncPlayer.Content = "⚡ Sync Player";
                }
            }
        }

        private void BtnExtensionHelp_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                string extDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "browser_extension");
                if (!Directory.Exists(extDir))
                {
                    string fallback = Path.Combine(Directory.GetCurrentDirectory(), "browser_extension");
                    if (Directory.Exists(fallback)) extDir = fallback;
                }

                if (Directory.Exists(extDir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{extDir}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/tousifkhan-web/SoundPair#readme",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExtensionHelp Error] {ex.Message}");
            }
        }

        #endregion

        private async void BtnCopyOffset_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null)
                {
                    string offsetText = globalOffsetMs > 0 ? $"-{globalOffsetMs} ms" : "0 ms";
                    await topLevel.Clipboard.SetTextAsync(offsetText);
                }
                if (btnCopyOffset != null) btnCopyOffset.Content = "Copied! ✓";
                await Task.Delay(1500);
                if (btnCopyOffset != null) btnCopyOffset.Content = "Copy Offset";
            }
            catch { }
        }

        #region Streaming Engine

        private async void BtnStart_Click(object? sender, RoutedEventArgs e)
        {
            if (isStreaming) return;
            if (isVisualCalibrating)
            {
                CloseSidePanel(true);
            }

            var dev1 = GetDeviceFromCombo(comboDevice1);
            var dev2 = GetDeviceFromCombo(comboDevice2);
            var dev3 = GetDeviceFromCombo(comboDevice3);

            if (dev1 == null && dev2 == null && dev3 == null)
            {
                ShowWarning("Please select at least one active output.");
                return;
            }

            try
            {
                isStreaming = true;
                btnStart.IsEnabled = false;

                // 1. Automatic "Silence This PC" speaker muting to prevent local room echo
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    defaultRenderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (defaultRenderDevice != null)
                    {
                        _originalSpeakerMuteState = defaultRenderDevice.AudioEndpointVolume.Mute;
                        cachedSysVol = defaultRenderDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                        bool shouldMute = chkMuteDefaultDevice?.IsChecked == true;
                        defaultRenderDevice.AudioEndpointVolume.Mute = shouldMute;
                        defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification += OnSystemVolumeNotification;
                    }
                }
                catch
                {
                    cachedSysVol = 1.0f;
                }

                lblStatus.Text = "Status: Waking up Bluetooth radios...";
                lblStatus.Foreground = Brushes.Orange;
                await ExecuteBluetoothHardwareHandshake(dev1, dev2, dev3);

                lblStatus.Text = "Status: Initializing native streams...";
                activeId1 = dev1?.ID;
                activeId2 = dev2?.ID;
                activeId3 = dev3?.ID;

                // 2. Native Windows 11 Process Loopback: Captures full pre-mute system audio across all apps with zero drivers
                uint currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                try
                {
                    capture = await new WasapiRecorderBuilder()
                        .WithProcessLoopback(currentPid, ProcessLoopbackMode.ExcludeTargetProcessTree)
                        .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
                        .BuildAsync();
                }
                catch
                {
                    // Graceful fallback if on an older Windows release
                    capture = new WasapiRecorderBuilder()
                        .WithLoopbackCapture()
                        .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
                        .Build();
                }

                // Reset pause states on start
                _isSlot1UserPaused = _isSlot2UserPaused = _isSlot3UserPaused = false;
                UpdateSlotPauseButtonUI(1, false);
                UpdateSlotPauseButtonUI(2, false);
                UpdateSlotPauseButtonUI(3, false);

                // Start playback on all active slots with independent, low-latency 800ms buffers
                if (dev1 != null) StartSlotPlayback(1, dev1);
                if (dev2 != null) StartSlotPlayback(2, dev2);
                if (dev3 != null) StartSlotPlayback(3, dev3);

                byte[] transferBuffer = new byte[8192];
                UpdateMaxBacklogLimits();

                capture.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
                {
                    try
                    {
                        if (buffer.Length > 0)
                        {
                            if (transferBuffer.Length < buffer.Length)
                            {
                                transferBuffer = new byte[buffer.Length];
                            }

                            if ((flags & AudioClientBufferFlags.Silent) != 0)
                            {
                                Array.Clear(transferBuffer, 0, buffer.Length);
                            }
                            else
                            {
                                buffer.CopyTo(transferBuffer);
                            }

                            // Independent, lock-free, isolated writes per slot:
                            // When one headset is paused (for phone call) or moves to a distance,
                            // its buffer stops receiving samples. Freeing Bluetooth radio bandwidth
                            // ensures the other active headset never stutters, breaks, or pauses!
                            var b1 = buffer1;
                            if (b1 != null && !_slot1Stalled && !_isSlot1UserPaused && b1.BufferedBytes <= _maxBacklogBytes1)
                            {
                                try { b1.AddSamples(transferBuffer, 0, buffer.Length); }
                                catch { _slot1Stalled = true; }
                            }

                            var b2 = buffer2;
                            if (b2 != null && !_slot2Stalled && !_isSlot2UserPaused && b2.BufferedBytes <= _maxBacklogBytes2)
                            {
                                try { b2.AddSamples(transferBuffer, 0, buffer.Length); }
                                catch { _slot2Stalled = true; }
                            }

                            var b3 = buffer3;
                            if (b3 != null && !_slot3Stalled && !_isSlot3UserPaused && b3.BufferedBytes <= _maxBacklogBytes3)
                            {
                                try { b3.AddSamples(transferBuffer, 0, buffer.Length); }
                                catch { _slot3Stalled = true; }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioCapture Error] {ex.Message}");
                    }
                };

                // Start capture immediately
                capture.StartRecording();

                ApplySystemAndAppVolumes();

                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;
                btnStop.Background = new SolidColorBrush(Color.FromRgb(210, 60, 80));
                btnStop.Foreground = Brushes.White;

                comboDevice1.IsEnabled = comboDevice2.IsEnabled = comboDevice3.IsEnabled = false;
                lblStatus.Text = "Status: Streaming live audio";
                lblStatus.Foreground = Brushes.LightGreen;

                // Sync with Browser Extension and active Media Players
                _browserSyncServer.IsStreaming = true;
                _browserSyncServer.CurrentOffsetMs = globalOffsetMs;
                if (globalOffsetMs > 0 && _mediaPlayerSyncService.IsAutoSyncEnabled)
                {
                    _ = _mediaPlayerSyncService.TrySyncActivePlayersAsync(globalOffsetMs);
                }
                _mediaPlayerWatcherTimer?.Start();
            }
            catch (Exception ex)
            {
                isStreaming = false;
                ShowError($"Stream failed: {ex.Message}");
                BtnStop_Click(null, null);
            }
        }

        private void OnSystemVolumeNotification(AudioVolumeNotificationData data)
        {
            cachedSysVol = data.MasterVolume;
            ApplySystemAndAppVolumes();

            if (chkMuteDefaultDevice != null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (chkMuteDefaultDevice != null && chkMuteDefaultDevice.IsChecked != data.Muted)
                    {
                        _isSyncingSpeakerMute = true;
                        chkMuteDefaultDevice.IsChecked = data.Muted;
                        _isSyncingSpeakerMute = false;
                    }
                });
            }
        }

        private void ChkMuteDefaultDevice_Changed(object? sender, RoutedEventArgs e)
        {
            if (_isSyncingSpeakerMute) return;
            bool shouldMute = chkMuteDefaultDevice?.IsChecked == true;

            if (isStreaming && defaultRenderDevice != null)
            {
                try
                {
                    defaultRenderDevice.AudioEndpointVolume.Mute = shouldMute;
                    if (lblStatus != null)
                    {
                        lblStatus.Text = shouldMute
                            ? "Status: Streaming live audio (PC speaker muted)"
                            : "Status: Streaming live audio (PC speaker unmuted)";
                        lblStatus.Foreground = Brushes.LightGreen;
                    }
                }
                catch { }
            }
        }

        private void SliderVol_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (lblVol1Val == null || lblVol2Val == null || lblVol3Val == null) return;

            if (sender == sliderVol1 && !_isSyncingVolume1)
            {
                _lastUserVol1ChangeTime = Stopwatch.GetTimestamp();
                lblVol1Val.Text = $"{(int)sliderVol1.Value}%";
                float volFactor = (float)(sliderVol1.Value / 100.0);

                if (sampleChannel1 != null)
                {
                    sampleChannel1.Volume = cachedSysVol * (boundDevice1 != null ? 1.0f : volFactor);
                }

                if (boundDevice1 != null)
                {
                    try
                    {
                        float clampedVol = Math.Max(0.0f, Math.Min(1.0f, volFactor));
                        if (Math.Abs(boundDevice1.AudioEndpointVolume.MasterVolumeLevelScalar - clampedVol) > 0.015f)
                            boundDevice1.AudioEndpointVolume.MasterVolumeLevelScalar = clampedVol;
                    }
                    catch { }
                }
            }
            else if (sender == sliderVol2 && !_isSyncingVolume2)
            {
                _lastUserVol2ChangeTime = Stopwatch.GetTimestamp();
                lblVol2Val.Text = $"{(int)sliderVol2.Value}%";
                float volFactor = (float)(sliderVol2.Value / 100.0);

                if (sampleChannel2 != null)
                {
                    sampleChannel2.Volume = cachedSysVol * (boundDevice2 != null ? 1.0f : volFactor);
                }

                if (boundDevice2 != null)
                {
                    try
                    {
                        float clampedVol = Math.Max(0.0f, Math.Min(1.0f, volFactor));
                        if (Math.Abs(boundDevice2.AudioEndpointVolume.MasterVolumeLevelScalar - clampedVol) > 0.015f)
                            boundDevice2.AudioEndpointVolume.MasterVolumeLevelScalar = clampedVol;
                    }
                    catch { }
                }
            }
            else if (sender == sliderVol3 && !_isSyncingVolume3)
            {
                _lastUserVol3ChangeTime = Stopwatch.GetTimestamp();
                lblVol3Val.Text = $"{(int)sliderVol3.Value}%";
                float volFactor = (float)(sliderVol3.Value / 100.0);

                if (sampleChannel3 != null)
                {
                    sampleChannel3.Volume = cachedSysVol * (boundDevice3 != null ? 1.0f : volFactor);
                }

                if (boundDevice3 != null)
                {
                    try
                    {
                        float clampedVol = Math.Max(0.0f, Math.Min(1.0f, volFactor));
                        if (Math.Abs(boundDevice3.AudioEndpointVolume.MasterVolumeLevelScalar - clampedVol) > 0.015f)
                            boundDevice3.AudioEndpointVolume.MasterVolumeLevelScalar = clampedVol;
                    }
                    catch { }
                }
            }
        }

        private bool _isUpdatingDelayText = false;

        private void BtnDelayDown_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int slot))
            {
                int current = slot switch { 1 => delay1Ms, 2 => delay2Ms, 3 => delay3Ms, _ => 0 };
                SetSlotDelay(slot, Math.Max(0, current - 10));
            }
        }

        private void BtnDelayUp_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int slot))
            {
                int current = slot switch { 1 => delay1Ms, 2 => delay2Ms, 3 => delay3Ms, _ => 0 };
                SetSlotDelay(slot, current + 10);
            }
        }

        private void BtnDelayReset_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int slot))
            {
                SetSlotDelay(slot, 0);
            }
        }

        private async void BtnDelaySave_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int slot))
            {
                var dev = slot switch
                {
                    1 => GetDeviceFromCombo(comboDevice1),
                    2 => GetDeviceFromCombo(comboDevice2),
                    3 => GetDeviceFromCombo(comboDevice3),
                    _ => null
                };

                if (dev == null)
                {
                    ShowWarning($"Please select an active output device for Slot {slot} first.");
                    return;
                }

                int currentDelay = slot switch
                {
                    1 => delay1Ms,
                    2 => delay2Ms,
                    3 => delay3Ms,
                    _ => 0
                };

                SaveDeviceDelay(dev.FriendlyName, currentDelay);
                SaveDeviceProfile(dev.FriendlyName, currentDelay);

                if (isStreaming)
                {
                    ApplyDelays();
                }

                if (lblStatus != null)
                {
                    lblStatus.Text = $"Status: Saved {currentDelay} ms delay for Slot {slot} ({dev.FriendlyName})!";
                    lblStatus.Foreground = Brushes.LightGreen;
                }

                object? originalContent = btn.Content;
                var originalBg = btn.Background;
                var originalFg = btn.Foreground;

                btn.Content = "Saved! ✓";
                btn.Background = new SolidColorBrush(Color.FromRgb(30, 58, 43));
                btn.Foreground = new SolidColorBrush(Color.FromRgb(112, 255, 160));
                await Task.Delay(1500);
                btn.Content = originalContent;
                btn.Background = originalBg;
                btn.Foreground = originalFg;
            }
        }

        private void TxtDelay_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (_isUpdatingDelayText) return;
            if (sender is TextBox tb && tb.Tag is string tagStr && int.TryParse(tagStr, out int slot))
            {
                string text = tb.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(text)) return;

                if (int.TryParse(text, out int parsed))
                {
                    int clamped = Math.Clamp(parsed, 0, 3000);
                    switch (slot)
                    {
                        case 1: delay1Ms = clamped; break;
                        case 2: delay2Ms = clamped; break;
                        case 3: delay3Ms = clamped; break;
                    }

                    if (isStreaming)
                    {
                        DebounceApplyDelays();
                    }
                }
            }
        }

        private void TxtDelay_LostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.Tag is string tagStr && int.TryParse(tagStr, out int slot))
            {
                int current = slot switch { 1 => delay1Ms, 2 => delay2Ms, 3 => delay3Ms, _ => 0 };
                _isUpdatingDelayText = true;
                try
                {
                    tb.Text = current.ToString();
                }
                finally
                {
                    _isUpdatingDelayText = false;
                }
            }
        }

        private void SetSlotDelay(int slotIndex, int newDelayMs, bool applyIfStreaming = true)
        {
            int clamped = Math.Clamp(newDelayMs, 0, 3000);
            _isUpdatingDelayText = true;
            try
            {
                switch (slotIndex)
                {
                    case 1:
                        delay1Ms = clamped;
                        if (txtDelay1 != null && txtDelay1.Text != clamped.ToString())
                            txtDelay1.Text = clamped.ToString();
                        break;
                    case 2:
                        delay2Ms = clamped;
                        if (txtDelay2 != null && txtDelay2.Text != clamped.ToString())
                            txtDelay2.Text = clamped.ToString();
                        break;
                    case 3:
                        delay3Ms = clamped;
                        if (txtDelay3 != null && txtDelay3.Text != clamped.ToString())
                            txtDelay3.Text = clamped.ToString();
                        break;
                }
            }
            finally
            {
                _isUpdatingDelayText = false;
            }

            UpdateMaxBacklogLimits();
            if (applyIfStreaming && isStreaming)
            {
                DebounceApplyDelays();
            }
        }

        private void DebounceApplyDelays()
        {
            if (_delayDebounceTimer == null)
            {
                _delayDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                _delayDebounceTimer.Tick += (s, ev) =>
                {
                    _delayDebounceTimer?.Stop();
                    if (isStreaming)
                    {
                        lock (_bufferLock)
                        {
                            ApplyDelays();
                        }
                    }
                };
            }
            _delayDebounceTimer.Stop();
            _delayDebounceTimer.Start();
        }

        private void ApplySystemAndAppVolumes()
        {
            if (sampleChannel1 != null) sampleChannel1.Volume = cachedSysVol * (boundDevice1 != null ? 1.0f : (float)(sliderVol1.Value / 100.0));
            if (sampleChannel2 != null) sampleChannel2.Volume = cachedSysVol * (boundDevice2 != null ? 1.0f : (float)(sliderVol2.Value / 100.0));
            if (sampleChannel3 != null) sampleChannel3.Volume = cachedSysVol * (boundDevice3 != null ? 1.0f : (float)(sliderVol3.Value / 100.0));
        }

        private void ApplyDelays()
        {
            if (capture == null) return;
            int blockAlign = capture.WaveFormat.BlockAlign;

            // Only apply silence delay padding to active, non-paused slots
            if (!_isSlot1UserPaused)
                ApplyBufferDelay(buffer1, GetBasePaddingMs(_isSlot1Wireless) + delay1Ms, blockAlign);
            if (!_isSlot2UserPaused)
                ApplyBufferDelay(buffer2, GetBasePaddingMs(_isSlot2Wireless) + delay2Ms, blockAlign);
            if (!_isSlot3UserPaused)
                ApplyBufferDelay(buffer3, GetBasePaddingMs(_isSlot3Wireless) + delay3Ms, blockAlign);

            UpdateMaxBacklogLimits();
        }

        private void ApplyBufferDelay(BufferedWaveProvider? buf, int delayMs, int blockAlign)
        {
            if (buf == null) return;
            buf.ClearBuffer();
            if (delayMs > 0 && capture != null)
            {
                long bytesNeeded = (long)capture.WaveFormat.AverageBytesPerSecond * delayMs / 1000;
                bytesNeeded -= bytesNeeded % blockAlign;

                if (bytesNeeded > 0)
                {
                    int toAdd = (int)Math.Min(bytesNeeded, (long)SilenceBuffer.Length);
                    buf.AddSamples(SilenceBuffer, 0, toAdd);
                }
            }
        }

        private void BtnStop_Click(object? sender, RoutedEventArgs? e)
        {
            _mediaPlayerWatcherTimer?.Stop();
            _mediaPlayerSyncService.ResetSyncedTracking();

            if (defaultRenderDevice != null)
            {
                try { defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification -= OnSystemVolumeNotification; } catch { }
                try { defaultRenderDevice.AudioEndpointVolume.Mute = _originalSpeakerMuteState; } catch { }
                defaultRenderDevice.Dispose();
                defaultRenderDevice = null;
            }

            try { capture?.StopRecording(); } catch { }
            try { out1?.Stop(); } catch { }
            try { out2?.Stop(); } catch { }
            try { out3?.Stop(); } catch { }

            capture?.Dispose(); capture = null;
            out1?.Dispose(); out1 = null;
            out2?.Dispose(); out2 = null;
            out3?.Dispose(); out3 = null;

            lock (_bufferLock)
            {
                buffer1 = buffer2 = buffer3 = null;
            }
            sampleChannel1 = sampleChannel2 = sampleChannel3 = null;
            activeId1 = activeId2 = activeId3 = null;
            _slot1Stalled = _slot2Stalled = _slot3Stalled = false;
            _isSlot1UserPaused = _isSlot2UserPaused = _isSlot3UserPaused = false;
            UpdateSlotPauseButtonUI(1, false);
            UpdateSlotPauseButtonUI(2, false);
            UpdateSlotPauseButtonUI(3, false);
            _maxBacklogBytes1 = _maxBacklogBytes2 = _maxBacklogBytes3 = int.MaxValue;

            isStreaming = false;
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            btnStop.Background = new SolidColorBrush(Color.FromRgb(51, 51, 51));
            btnStop.Foreground = Brushes.White;

            comboDevice1.IsEnabled = comboDevice2.IsEnabled = comboDevice3.IsEnabled = true;
            lblStatus.Text = "Status: Stopped";
            lblStatus.Foreground = TextSecBrush;

            _browserSyncServer.IsStreaming = false;
            if (lblPlayerSyncStatus != null)
            {
                lblPlayerSyncStatus.Text = "⚡ Auto-Sync: Idle (Ready for PotPlayer / VLC / MPC-HC)";
                lblPlayerSyncStatus.Foreground = new SolidColorBrush(Color.FromRgb(160, 174, 192));
            }
        }

        #endregion

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            try
            {
                _mediaPlayerWatcherTimer?.Stop();
                _mediaPlayerWatcherTimer = null;
                _browserSyncServer.Stop();

                debounceTimer?.Stop();
                debounceTimer = null;
                _delayDebounceTimer?.Stop();
                _delayDebounceTimer = null;

                if (_hwnd != IntPtr.Zero && _subclassProc != null)
                {
                    RemoveWindowSubclass(_hwnd, _subclassProc, (UIntPtr)101);
                }
            }
            catch { }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                CloseSidePanel(true);
                UnbindAllDevices();

                if (_trayIcon != null)
                {
                    _trayIcon.IsVisible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }

                RestoreOriginalDefaultDevice();

                BtnStop_Click(null, null);
                foreach (var d in _cachedDevices)
                {
                    try { d.Dispose(); } catch { }
                }
                _cachedDevices.Clear();

                _browserSyncServer.Dispose();
            }
            catch { }

            base.OnClosed(e);
        }
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class PolicyConfigClient { }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr ppFormat);
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, out IntPtr ppFormat);
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, out IntPtr pmftDefaultPeriod, out IntPtr pmftMinimumPeriod);
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr pMode);
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, out IntPtr pv);
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bFxStore, IntPtr key, IntPtr pv);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, Role eRole);
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bVisible);
    }
}
