using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundPair
{
    public partial class MainWindow : Window
    {
        private WasapiRecorder? capture;
        private BufferedWaveProvider? buffer1, buffer2, buffer3;
        private SampleChannel? sampleChannel1, sampleChannel2, sampleChannel3;
        private WasapiPlayer? out1, out2, out3;

        private readonly List<MMDevice> _cachedDevices = new();
        private static readonly HttpClient SharedHttpClient = new HttpClient();
        private DispatcherTimer? _delayDebounceTimer;
        private MMDevice? defaultRenderDevice;

        // Hardware volume sync states
        private MMDevice? boundDevice1, boundDevice2, boundDevice3;
        private bool _isSyncingVolume1, _isSyncingVolume2, _isSyncingVolume3;

        private bool isStreaming = false;
        private bool hasCheckedVbCable = false;
        private bool isInitialLoad = true;

        private int delay1Ms = 0;
        private int delay2Ms = 0;
        private int delay3Ms = 0;
        private int globalOffsetMs = 0;

        // Baseline buffer padding to prevent initial WASAPI shared-mode underruns
        private const int BaseDelayMs = 40;
        private const int WM_DEVICECHANGE = 0x0219;

        private float cachedSysVol = 1.0f;
        private string? activeId1, activeId2, activeId3;
        private string? originalDefaultDeviceId = null;
        private bool didChangeDefaultDevice = false;

        private System.Windows.Forms.NotifyIcon? notifyIcon;
        private DispatcherTimer? debounceTimer;
        private static readonly byte[] SilenceBuffer = new byte[1048576]; // 1MB zero buffer

        // Synchronization lock for real-time audio buffer operations across UI & Audio threads
        private readonly object _bufferLock = new object();

        // Persistent Device Profiles
        private readonly string profilesFilePath;
        private Dictionary<string, int> deviceProfiles = new(StringComparer.OrdinalIgnoreCase);

        // Visual Calibration Studio
        private bool isVisualCalibrating = false;
        private int _calActiveSlot = 0; // 0 = Global, 1 = Slot 1, 2 = Slot 2, 3 = Slot 3
        private WasapiPlayer? _calPlayer;
        private CalibrationClickProvider? _calClickProvider;
        private readonly List<(WasapiPlayer player, CalibrationClickProvider provider, int slotIndex)> _globalCalOutputs = new();
        private readonly Stopwatch _dropStopwatch = new Stopwatch();
        private DispatcherTimer? _autoDropTimer;
        private bool _isDropping = false;
        private bool _hasImpacted = false;
        private bool _awaitingTap = false;
        private long _impactTimestamp = 0;
        private readonly List<double> _recordedRawTaps = new List<double>();
        private int _nudgeOffsetMs = 0;
        private const double DropDurationMs = 850.0;
        private const double StartY = 8.0;
        private const double TargetY = 110.0;
        private const double BallDiameter = 22.0;

        public MainWindow()
        {
            InitializeComponent();
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

            SaveOriginalDefaultDevice();
            SetVBCableAsSystemDefault();
            LoadAudioDevices();
        }

        private void InitializeTrayIcon()
        {
            notifyIcon = new System.Windows.Forms.NotifyIcon();
            try
            {
                var iconStream = System.Windows.Application.GetResourceStream(new Uri("SoundPair.ico", UriKind.Relative))?.Stream;
                if (iconStream != null)
                {
                    notifyIcon.Icon = new System.Drawing.Icon(iconStream);
                }
                else
                {
                    notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                }
            }
            catch
            {
                notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            notifyIcon.Text = "Sound Pair";
            notifyIcon.Visible = false;
            notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    RestoreFromTray();
                }
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("Open", null, (s, e) => RestoreFromTray());
            contextMenu.Items.Add("Exit", null, (s, e) => Close());
            notifyIcon.ContextMenuStrip = contextMenu;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                this.Hide();
                if (notifyIcon != null)
                {
                    notifyIcon.Visible = true;
                }
            }
        }

        private void RestoreFromTray()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
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

        private void ClearDeviceProfile(string deviceName)
        {
            try
            {
                string pure = GetPureDeviceName(deviceName) ?? deviceName;
                deviceProfiles.Remove(pure);
                deviceProfiles.Remove(deviceName);

                var toRemove = deviceProfiles.Keys.Where(k =>
                    k.Equals(pure, StringComparison.OrdinalIgnoreCase) ||
                    k.Equals(deviceName, StringComparison.OrdinalIgnoreCase) ||
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

        private void TryAutoLoadProfilesForActiveDevices()
        {
            if (isStreaming || isInitialLoad || _isDropping) return;

            var dev1 = GetDeviceFromCombo(comboDevice1);
            var dev2 = GetDeviceFromCombo(comboDevice2);
            var dev3 = GetDeviceFromCombo(comboDevice3);

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
                globalOffsetMs = maxLat;
                if (lblGlobalOffset != null) lblGlobalOffset.Text = $"-{globalOffsetMs} ms";

                if (sliderDelay1 != null) sliderDelay1.Value = l1.HasValue ? Math.Max(0, maxLat - l1.Value) : 0;
                if (sliderDelay2 != null) sliderDelay2.Value = l2.HasValue ? Math.Max(0, maxLat - l2.Value) : 0;
                if (sliderDelay3 != null) sliderDelay3.Value = l3.HasValue ? Math.Max(0, maxLat - l3.Value) : 0;

                if (lblStatus != null)
                {
                    lblStatus.Text = $"Status: Balanced delays from saved profiles (Max: {maxLat} ms)";
                    lblStatus.Foreground = Brushes.LightGreen;
                }
            }
            else if (valid.Count == 1)
            {
                int singleLat = valid[0];
                globalOffsetMs = singleLat;
                if (lblGlobalOffset != null) lblGlobalOffset.Text = $"-{globalOffsetMs} ms";

                if (sliderDelay1 != null) sliderDelay1.Value = 0;
                if (sliderDelay2 != null) sliderDelay2.Value = 0;
                if (sliderDelay3 != null) sliderDelay3.Value = 0;

                if (lblStatus != null)
                {
                    lblStatus.Text = $"Status: Loaded saved profile ({singleLat} ms)";
                    lblStatus.Foreground = Brushes.LightGreen;
                }
            }
            else
            {
                globalOffsetMs = 0;
                if (lblGlobalOffset != null) lblGlobalOffset.Text = "0 ms";
                if (sliderDelay1 != null) sliderDelay1.Value = 0;
                if (sliderDelay2 != null) sliderDelay2.Value = 0;
                if (sliderDelay3 != null) sliderDelay3.Value = 0;
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

        private void SaveOriginalDefaultDevice()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var defaultDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (defaultDev != null)
                {
                    if (IsVbCable(defaultDev))
                    {
                        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                        foreach (var dev in endpoints)
                        {
                            if (!IsVbCable(dev))
                            {
                                originalDefaultDeviceId = dev.ID;
                                break;
                            }
                        }
                    }
                    else
                    {
                        originalDefaultDeviceId = defaultDev.ID;
                    }
                }
            }
            catch { }
        }

        private void SetVBCableAsSystemDefault()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                foreach (var device in endpoints)
                {
                    if (IsVbCable(device))
                    {
                        if (SetSystemDefaultAudioDevice(device.ID))
                        {
                            didChangeDefaultDevice = true;
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        private bool SetSystemDefaultAudioDevice(string deviceId)
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

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            ApplyDarkTitleBar(helper.Handle);
            HwndSource.FromHwnd(helper.Handle)?.AddHook(HwndHook);
        }

        private void ApplyDarkTitleBar(IntPtr hwnd)
        {
            int useImmersiveDarkMode = 1;
            int hr = DwmSetWindowAttribute(hwnd, 20, ref useImmersiveDarkMode, sizeof(int));
            if (hr != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref useImmersiveDarkMode, sizeof(int));
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                if (debounceTimer == null)
                {
                    debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    debounceTimer.Tick += (s, args) =>
                    {
                        debounceTimer.Stop();

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
            }
            return IntPtr.Zero;
        }

        private void HandleDeviceChangeWhileStreaming()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var activeEndpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < activeEndpoints.Count; i++)
                {
                    using var d = activeEndpoints[i];
                    activeIds.Add(d.ID);
                }

                bool IsAlive(string? id) => id != null && activeIds.Contains(id);

                if (activeId1 != null && !IsAlive(activeId1))
                    KillStream(ref out1, ref buffer1, ref sampleChannel1, ref activeId1);

                if (activeId2 != null && !IsAlive(activeId2))
                    KillStream(ref out2, ref buffer2, ref sampleChannel2, ref activeId2);

                if (activeId3 != null && !IsAlive(activeId3))
                    KillStream(ref out3, ref buffer3, ref sampleChannel3, ref activeId3);

                if (out1 == null && out2 == null && out3 == null)
                {
                    BtnStop_Click(null, null);
                }
            }
            catch { }
        }

        private void KillStream(ref WasapiPlayer? player, ref BufferedWaveProvider? buffer, ref SampleChannel? channel, ref string? activeId)
        {
            try { player?.Stop(); } catch { }
            try { player?.Dispose(); } catch { }

            player = null;
            lock (_bufferLock)
            {
                buffer = null;
            }
            channel = null;
            activeId = null;
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
            Dispatcher.InvokeAsync(() =>
            {
                double newVol = data.MasterVolume * 100;
                if (Math.Abs(sliderVol1.Value - newVol) > 1.0)
                {
                    _isSyncingVolume1 = true;
                    sliderVol1.Value = newVol;
                    lblVol1Val.Text = $"{(int)newVol}%";
                    _isSyncingVolume1 = false;
                }
            });
        }

        private void OnDevice2VolumeChanged(AudioVolumeNotificationData data)
        {
            Dispatcher.InvokeAsync(() =>
            {
                double newVol = data.MasterVolume * 100;
                if (Math.Abs(sliderVol2.Value - newVol) > 1.0)
                {
                    _isSyncingVolume2 = true;
                    sliderVol2.Value = newVol;
                    lblVol2Val.Text = $"{(int)newVol}%";
                    _isSyncingVolume2 = false;
                }
            });
        }

        private void OnDevice3VolumeChanged(AudioVolumeNotificationData data)
        {
            Dispatcher.InvokeAsync(() =>
            {
                double newVol = data.MasterVolume * 100;
                if (Math.Abs(sliderVol3.Value - newVol) > 1.0)
                {
                    _isSyncingVolume3 = true;
                    sliderVol3.Value = newVol;
                    lblVol3Val.Text = $"{(int)newVol}%";
                    _isSyncingVolume3 = false;
                }
            });
        }

        private void ComboDevice1_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => BindDevice1(GetDeviceFromCombo(comboDevice1));
        private void ComboDevice2_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => BindDevice2(GetDeviceFromCombo(comboDevice2));
        private void ComboDevice3_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => BindDevice3(GetDeviceFromCombo(comboDevice3));

        // -------------------------------------------

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

                comboDevice1.Items.Clear();
                comboDevice2.Items.Clear();
                comboDevice3.Items.Clear();

                comboDevice1.Items.Add("-- Disabled / Blank --");
                comboDevice2.Items.Add("-- Disabled / Blank --");
                comboDevice3.Items.Add("-- Disabled / Blank --");

                foreach (var d in _cachedDevices)
                {
                    try { d.Dispose(); } catch { }
                }
                _cachedDevices.Clear();

                using var enumerator = new MMDeviceEnumerator();
                using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                bool vbCableFound = false;

                for (int i = 0; i < endpoints.Count; i++)
                {
                    var dev = endpoints[i];
                    _cachedDevices.Add(dev);
                    string name = dev.FriendlyName;
                    comboDevice1.Items.Add($"[{i}] {name}");
                    comboDevice2.Items.Add($"[{i}] {name}");
                    comboDevice3.Items.Add($"[{i}] {name}");

                    if (name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
                    {
                        vbCableFound = true;
                    }
                }

                comboDevice1.SelectionChanged += ComboDevice1_SelectionChanged;
                comboDevice2.SelectionChanged += ComboDevice2_SelectionChanged;
                comboDevice3.SelectionChanged += ComboDevice3_SelectionChanged;

                SelectDeviceByName(comboDevice1, selectedName1, _cachedDevices, isInitialLoad && _cachedDevices.Count > 0 ? 1 : 0);
                SelectDeviceByName(comboDevice2, selectedName2, _cachedDevices, 0);
                SelectDeviceByName(comboDevice3, selectedName3, _cachedDevices, 0);

                isInitialLoad = false;
                TryAutoLoadProfilesForActiveDevices();

                if (!vbCableFound && !hasCheckedVbCable)
                {
                    hasCheckedVbCable = true;
                    var result = MessageBox.Show(
                        "VB-Cable Virtual Audio Driver was not detected.\n\nThis driver is required to route clean loopback audio without echoing. Would you like to download and install it automatically?",
                        "Virtual Audio Driver Required",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        _ = AutoInstallVBCable();
                    }
                }
                else if (vbCableFound)
                {
                    hasCheckedVbCable = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading devices: {ex.Message}");
            }
        }

        private async Task AutoInstallVBCable()
        {
            lblStatus.Text = "Status: Downloading VB-Cable...";
            lblStatus.Foreground = Brushes.Orange;
            btnStart.IsEnabled = false;

            string zipUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
            string tempFolder = Path.Combine(Path.GetTempPath(), "SoundPair_VBCable_" + Guid.NewGuid().ToString());
            string zipPath = Path.Combine(tempFolder, "vbcable.zip");

            try
            {
                if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
                Directory.CreateDirectory(tempFolder);

                var response = await SharedHttpClient.GetByteArrayAsync(zipUrl);
                await File.WriteAllBytesAsync(zipPath, response);

                lblStatus.Text = "Status: Extracting installer...";

                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tempFolder));

                lblStatus.Text = "Status: Waiting for installation...";
                string installerName = Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe";
                string installerPath = Path.Combine(tempFolder, installerName);

                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                System.Diagnostics.Process.Start(processInfo);
                MessageBox.Show("VB-Cable installer launched. Please complete the setup steps, then restart Sound Pair.", "Installation", MessageBoxButton.OK, MessageBoxImage.Information);
                lblStatus.Text = "Status: Restart required.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Auto-install failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://vb-audio.com/Cable/", UseShellExecute = true });
                lblStatus.Text = "Status: Ready";
                btnStart.IsEnabled = true;
            }
        }

        private string? GetPureDeviceName(string? comboItemText)
        {
            if (string.IsNullOrEmpty(comboItemText) || comboItemText.StartsWith("--"))
                return null;

            int idx = comboItemText.IndexOf(']');
            if (idx >= 0 && idx + 2 < comboItemText.Length)
            {
                return comboItemText.Substring(idx + 2);
            }
            return comboItemText;
        }

        private void SelectDeviceByName(System.Windows.Controls.ComboBox cb, string? targetName, List<MMDevice> activeDevices, int defaultIdx)
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

            if (cb.Items.Count > defaultIdx)
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

        private MMDevice? GetDeviceFromCombo(System.Windows.Controls.ComboBox cb)
        {
            if (cb.SelectedIndex <= 0 || (cb.SelectedIndex - 1) >= _cachedDevices.Count) return null;
            return _cachedDevices[cb.SelectedIndex - 1];
        }

        private async Task ExecuteBluetoothHardwareHandshake(MMDevice? dev1, MMDevice? dev2, MMDevice? dev3)
        {
            var devicesToWake = new[] { dev1, dev2, dev3 }.Where(d => d != null && IsWirelessDevice(d)).ToList();
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

        private void BtnGlobalCalibrate_Click(object sender, RoutedEventArgs e) => OpenSidePanelForSlot(0);
        private void BtnCalDevice1_Click(object sender, RoutedEventArgs e) => OpenSidePanelForSlot(1);
        private void BtnCalDevice2_Click(object sender, RoutedEventArgs e) => OpenSidePanelForSlot(2);
        private void BtnCalDevice3_Click(object sender, RoutedEventArgs e) => OpenSidePanelForSlot(3);

        private void OpenSidePanelForSlot(int slotIndex)
        {
            if (isStreaming)
            {
                var res = MessageBox.Show(
                    "Streaming is currently running.\n\nCalibration requires direct access to output devices. Stop streaming to calibrate?",
                    "Pause Streaming For Calibration",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (res == MessageBoxResult.Yes)
                {
                    BtnStop_Click(null, null);
                }
                else
                {
                    return;
                }
            }

            isVisualCalibrating = true;
            borderSidePanel.Visibility = Visibility.Visible;
            this.MinWidth = 940;
            this.Width = 940;

            SelectCalibrationSlot(slotIndex);
        }

        private void BtnCloseSidePanel_Click(object sender, RoutedEventArgs e)
        {
            CloseSidePanel();
        }

        private void CloseSidePanel()
        {
            _autoDropTimer?.Stop();
            _autoDropTimer = null;
            StopDrop();
            StopCalAudio();

            isVisualCalibrating = false;
            borderSidePanel.Visibility = Visibility.Collapsed;
            this.MinWidth = 480;
            this.Width = 500;
        }

        private void BtnTabSlot_Click(object sender, RoutedEventArgs e)
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
                btnApplyCal.Content = "💾 Apply";
            }

            var activeBrush = new SolidColorBrush(Color.FromRgb(36, 52, 71));
            var activeFore = (SolidColorBrush)FindResource("Highlight");
            var idleBrush = new SolidColorBrush(Color.FromRgb(46, 46, 46));
            var idleFore = new SolidColorBrush(Color.FromRgb(170, 170, 170));

            if (btnTabGlobal != null) { btnTabGlobal.Background = slotIndex == 0 ? activeBrush : idleBrush; btnTabGlobal.Foreground = slotIndex == 0 ? activeFore : idleFore; }
            if (btnTabSlot1 != null) { btnTabSlot1.Background = slotIndex == 1 ? activeBrush : idleBrush; btnTabSlot1.Foreground = slotIndex == 1 ? activeFore : idleFore; }
            if (btnTabSlot2 != null) { btnTabSlot2.Background = slotIndex == 2 ? activeBrush : idleBrush; btnTabSlot2.Foreground = slotIndex == 2 ? activeFore : idleFore; }
            if (btnTabSlot3 != null) { btnTabSlot3.Background = slotIndex == 3 ? activeBrush : idleBrush; btnTabSlot3.Foreground = slotIndex == 3 ? activeFore : idleFore; }

            UpdateCalibrationDisplayForCurrentSlot();
            StartCalAudioForCurrentSlot();
        }

        private void UpdateCalibrationDisplayForCurrentSlot()
        {
            if (!isVisualCalibrating || borderSidePanel == null || borderSidePanel.Visibility != Visibility.Visible) return;

            // Reaction compensation & fine-tune card is active for both Global and Slot modes
            if (borderReactionTuneCard != null) borderReactionTuneCard.Visibility = Visibility.Visible;
            if (btnTriggerDrop != null) btnTriggerDrop.Content = "▶ Start Drop";
            if (btnApplyCal != null) btnApplyCal.Content = "💾 Apply";
            if (btnClearSaved != null) btnClearSaved.Content = "🗑️ Clear";

            if (_calActiveSlot == 0)
            {
                // Global section: no delay sliders card
                if (borderSlotDelayCard != null) borderSlotDelayCard.Visibility = Visibility.Collapsed;

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
                    btnClearSaved.ToolTip = "Clear all saved calibration profiles and reset delays to 0 ms";
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

            if (lblCalModeHeader != null) lblCalModeHeader.Text = $"Slot {_calActiveSlot}: ";
            if (btnClearSaved != null) btnClearSaved.ToolTip = $"Clear saved calibration profile for Slot {_calActiveSlot} and reset delay to 0 ms";

            // Show slot delay card with only the corresponding slot slider
            if (borderSlotDelayCard != null)
            {
                borderSlotDelayCard.Visibility = Visibility.Visible;
                if (lblDelayCardHeader != null) lblDelayCardHeader.Text = $"⏱ Slot {_calActiveSlot} Delay Offset";
                if (gridDelaySlot1 != null) gridDelaySlot1.Visibility = _calActiveSlot == 1 ? Visibility.Visible : Visibility.Collapsed;
                if (gridDelaySlot2 != null) gridDelaySlot2.Visibility = _calActiveSlot == 2 ? Visibility.Visible : Visibility.Collapsed;
                if (gridDelaySlot3 != null) gridDelaySlot3.Visibility = _calActiveSlot == 3 ? Visibility.Visible : Visibility.Collapsed;
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

                int? savedLat = GetSavedProfileLatency(dev.FriendlyName);
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
            StopCalAudio();

            if (_calActiveSlot == 0)
            {
                var activeDevs = new (MMDevice? dev, int slot)[]
                {
                    (GetDeviceFromCombo(comboDevice1), 1),
                    (GetDeviceFromCombo(comboDevice2), 2),
                    (GetDeviceFromCombo(comboDevice3), 3)
                };

                foreach (var (dev, slot) in activeDevs)
                {
                    if (dev == null) continue;
                    try
                    {
                        var mixFormat = GetDeviceMixFormat(dev);
                        var provider = new CalibrationClickProvider(mixFormat.SampleRate, mixFormat.Channels);
                        var player = new WasapiPlayerBuilder().WithDevice(dev).Build();
                        player.Init(provider);
                        player.Play();
                        _globalCalOutputs.Add((player, provider, slot));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[GlobalCalAudio Error] {ex.Message}");
                    }
                }
                return;
            }

            var singleDev = GetActiveCalDevice();
            if (singleDev == null) return;

            try
            {
                var mixFormat = GetDeviceMixFormat(singleDev);
                _calClickProvider = new CalibrationClickProvider(mixFormat.SampleRate, mixFormat.Channels);
                _calPlayer = new WasapiPlayerBuilder().WithDevice(singleDev).Build();
                _calPlayer.Init(_calClickProvider);
                _calPlayer.Play();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CalAudio Error] {ex.Message}");
            }
        }

        private void StopCalAudio()
        {
            foreach (var (player, _, _) in _globalCalOutputs)
            {
                try { player.Stop(); } catch { }
                try { player.Dispose(); } catch { }
            }
            _globalCalOutputs.Clear();

            try { _calPlayer?.Stop(); } catch { }
            try { _calPlayer?.Dispose(); } catch { }
            _calPlayer = null;
            _calClickProvider = null;
        }

        private void BtnTriggerDrop_Click(object sender, RoutedEventArgs e)
        {
            StartDrop();
        }

        private void StartDrop()
        {
            if (_isDropping) return;

            if (_calActiveSlot == 0)
            {
                var d1 = GetDeviceFromCombo(comboDevice1);
                var d2 = GetDeviceFromCombo(comboDevice2);
                var d3 = GetDeviceFromCombo(comboDevice3);
                if (d1 == null && d2 == null && d3 == null)
                {
                    MessageBox.Show("Please select at least one active output device first.");
                    return;
                }
                if (_globalCalOutputs.Count == 0)
                {
                    StartCalAudioForCurrentSlot();
                }
            }
            else
            {
                var dev = GetActiveCalDevice();
                if (dev == null)
                {
                    MessageBox.Show($"Please select an active device for Slot {_calActiveSlot} first.");
                    return;
                }

                if (_calPlayer == null)
                {
                    StartCalAudioForCurrentSlot();
                }
            }

            _isDropping = true;
            _hasImpacted = false;
            _awaitingTap = false;

            lblStagePrompt.Text = "Dropping... Watch the ball & listen!";
            lblStagePrompt.Foreground = (SolidColorBrush)FindResource("Highlight");
            borderFeedback.Visibility = Visibility.Collapsed;

            Canvas.SetTop(ballElement, StartY);
            rippleRing.Opacity = 0;

            _dropStopwatch.Restart();
            CompositionTarget.Rendering += OnRenderingDrop;
        }

        private void OnRenderingDrop(object? sender, EventArgs e)
        {
            double elapsed = _dropStopwatch.Elapsed.TotalMilliseconds;
            double progress = Math.Clamp(elapsed / DropDurationMs, 0.0, 1.0);

            // Gravity acceleration curve (Ease-In: progress^1.8)
            double eased = Math.Pow(progress, 1.8);
            double targetTop = TargetY - BallDiameter; // 190 - 28 = 162
            double currentTop = StartY + (targetTop - StartY) * eased;
            Canvas.SetTop(ballElement, currentTop);

            if (elapsed >= DropDurationMs && !_hasImpacted)
            {
                _hasImpacted = true;
                OnBallImpact();
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
            }
        }

        private void OnBallImpact()
        {
            _impactTimestamp = Stopwatch.GetTimestamp();
            _awaitingTap = true;

            if (_calActiveSlot == 0)
            {
                // Global mode: play transient to all active outputs with their calibrated delay offsets
                foreach (var (_, provider, slot) in _globalCalOutputs)
                {
                    int delay = slot switch
                    {
                        1 => delay1Ms,
                        2 => delay2Ms,
                        3 => delay3Ms,
                        _ => 0
                    };
                    provider.Trigger(delay);
                }
            }
            else
            {
                // Single slot mode
                _calClickProvider?.Trigger(0);
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

            var sizeAnim = new System.Windows.Media.Animation.DoubleAnimation(4, 55, TimeSpan.FromMilliseconds(260));
            var opacityAnim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(260));
            var leftAnim = new System.Windows.Media.Animation.DoubleAnimation(210 - 2, 210 - 27.5, TimeSpan.FromMilliseconds(260));
            var topAnim = new System.Windows.Media.Animation.DoubleAnimation(TargetY - 2, TargetY - 27.5, TimeSpan.FromMilliseconds(260));

            rippleRing.BeginAnimation(WidthProperty, sizeAnim);
            rippleRing.BeginAnimation(HeightProperty, sizeAnim);
            rippleRing.BeginAnimation(Canvas.LeftProperty, leftAnim);
            rippleRing.BeginAnimation(Canvas.TopProperty, topAnim);
            rippleRing.BeginAnimation(OpacityProperty, opacityAnim);
        }

        private void StopDrop()
        {
            _autoDropTimer?.Stop();
            _autoDropTimer = null;
            _isDropping = false;
            CompositionTarget.Rendering -= OnRenderingDrop;
            Canvas.SetTop(ballElement, StartY);
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (comboDevice1.IsDropDownOpen || comboDevice2.IsDropDownOpen || comboDevice3.IsDropDownOpen) return;

            if (isVisualCalibrating && borderSidePanel.Visibility == Visibility.Visible && e.Key == Key.Space)
            {
                e.Handled = true; // Prevent activating any focused UI button
                HandleSpacebarTap();
            }
        }

        private void HandleSpacebarTap()
        {
            if (!_isDropping)
            {
                StartDrop();
                return;
            }

            if (!_hasImpacted)
            {
                lblStagePrompt.Text = "⚠️ Too early! Wait until you HEAR the sound.";
                lblStagePrompt.Foreground = Brushes.Orange;
                borderFeedback.Visibility = Visibility.Visible;
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
                borderFeedback.Visibility = Visibility.Visible;
                lblFeedbackText.Text = $"Tap: {rawDeltaMs:F0} ms";
                lblFeedbackText.Foreground = (SolidColorBrush)FindResource("Highlight");

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
                    btnApplyCal.Content = "💾 Apply";
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
                btnApplyCal.Content = "💾 Apply";
                return;
            }

            lblTapHistory.Text = string.Join(" | ", _recordedRawTaps.Select((t, i) => $"#{i + 1}: {t:F0}ms"));
            int eff = CalculateEffectiveLatency();
            lblAvgLatency.Text = $"{eff} ms";
            btnApplyCal.IsEnabled = true;
            btnApplyCal.Content = "💾 Apply";
        }

        private void ChkHumanReaction_Changed(object sender, RoutedEventArgs e)
        {
            if (lblTapHistory == null || lblAvgLatency == null || btnApplyCal == null) return;
            UpdateTapDisplay();
        }

        private void BtnResetTaps_Click(object sender, RoutedEventArgs e)
        {
            _autoDropTimer?.Stop();
            _autoDropTimer = null;
            _recordedRawTaps.Clear();
            _nudgeOffsetMs = 0;
            if (lblFineTuneVal != null) lblFineTuneVal.Text = "0 ms";
            if (borderFeedback != null) borderFeedback.Visibility = Visibility.Collapsed;
            if (lblStagePrompt != null)
            {
                lblStagePrompt.Text = "Taps reset. Click 'Start Drop' or press [Spacebar]!";
                lblStagePrompt.Foreground = Brushes.White;
            }
            UpdateTapDisplay();
        }

        private void BtnNudgeDown_Click(object sender, RoutedEventArgs e)
        {
            _nudgeOffsetMs -= 5;
            if (lblFineTuneVal != null) lblFineTuneVal.Text = $"{_nudgeOffsetMs:+#;-#;0} ms";
            UpdateTapDisplay();
        }

        private void BtnNudgeUp_Click(object sender, RoutedEventArgs e)
        {
            _nudgeOffsetMs += 5;
            if (lblFineTuneVal != null) lblFineTuneVal.Text = $"{_nudgeOffsetMs:+#;-#;0} ms";
            UpdateTapDisplay();
        }

        private async void BtnApplyCal_Click(object sender, RoutedEventArgs e)
        {
            if (_calActiveSlot == 0)
            {
                var d1 = GetDeviceFromCombo(comboDevice1);
                var d2 = GetDeviceFromCombo(comboDevice2);
                var d3 = GetDeviceFromCombo(comboDevice3);

                if (d1 == null && d2 == null && d3 == null)
                {
                    MessageBox.Show("Please select at least one active output device first.");
                    return;
                }

                int finalLatency = CalculateEffectiveLatency();
                if (finalLatency <= 0 && _recordedRawTaps.Count == 0 && globalOffsetMs > 0)
                {
                    finalLatency = globalOffsetMs;
                }

                // 1. Set global Media Lip-Sync Offset
                globalOffsetMs = finalLatency;
                if (lblGlobalOffset != null) lblGlobalOffset.Text = $"-{globalOffsetMs} ms";

                // 2. Calibrate and save all active devices to this latency
                if (d1 != null) SaveDeviceProfile(d1.FriendlyName, finalLatency);
                if (d2 != null) SaveDeviceProfile(d2.FriendlyName, finalLatency);
                if (d3 != null) SaveDeviceProfile(d3.FriendlyName, finalLatency);

                // 3. Reset delay sliders to 0 since all devices calibrated together in unison
                if (sliderDelay1 != null) sliderDelay1.Value = 0;
                if (sliderDelay2 != null) sliderDelay2.Value = 0;
                if (sliderDelay3 != null) sliderDelay3.Value = 0;
                delay1Ms = delay2Ms = delay3Ms = 0;

                // 4. Update audio delays in real-time if streaming
                if (isStreaming)
                {
                    ApplyDelays();
                }

                lblStatus.Text = $"Status: Global calibration applied & saved for all devices at {finalLatency} ms! (Lip-Sync: -{finalLatency} ms)";
                lblStatus.Foreground = Brushes.LightGreen;

                btnApplyCal.Content = "Saved! ✓";
                await Task.Delay(1500);
                if (btnApplyCal != null) btnApplyCal.Content = "💾 Apply";
                return;
            }

            var dev = GetActiveCalDevice();
            if (dev == null) return;

            int slotLatency = CalculateEffectiveLatency();
            SaveDeviceProfile(dev.FriendlyName, slotLatency);

            // Automatically recalculate multi-device delays and lip-sync offset
            TryAutoLoadProfilesForActiveDevices();

            lblStatus.Text = $"Status: Slot {_calActiveSlot} ({dev.FriendlyName}) calibrated & saved at {slotLatency} ms!";
            lblStatus.Foreground = Brushes.LightGreen;

            btnApplyCal.Content = "Saved! ✓";
            await Task.Delay(1500);
            if (btnApplyCal != null) btnApplyCal.Content = "💾 Apply";
        }

        private async void BtnClearSaved_Click(object sender, RoutedEventArgs e)
        {
            if (_calActiveSlot == 0)
            {
                var d1 = GetDeviceFromCombo(comboDevice1);
                var d2 = GetDeviceFromCombo(comboDevice2);
                var d3 = GetDeviceFromCombo(comboDevice3);

                if (d1 != null) ClearDeviceProfile(d1.FriendlyName);
                if (d2 != null) ClearDeviceProfile(d2.FriendlyName);
                if (d3 != null) ClearDeviceProfile(d3.FriendlyName);

                if (sliderDelay1 != null) sliderDelay1.Value = 0;
                if (sliderDelay2 != null) sliderDelay2.Value = 0;
                if (sliderDelay3 != null) sliderDelay3.Value = 0;
                delay1Ms = delay2Ms = delay3Ms = 0;
                globalOffsetMs = 0;
                if (lblGlobalOffset != null) lblGlobalOffset.Text = "0 ms";

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

                    if (_calActiveSlot == 1 && sliderDelay1 != null) { sliderDelay1.Value = 0; delay1Ms = 0; }
                    else if (_calActiveSlot == 2 && sliderDelay2 != null) { sliderDelay2.Value = 0; delay2Ms = 0; }
                    else if (_calActiveSlot == 3 && sliderDelay3 != null) { sliderDelay3.Value = 0; delay3Ms = 0; }

                    _recordedRawTaps.Clear();
                    _nudgeOffsetMs = 0;
                    if (lblFineTuneVal != null) lblFineTuneVal.Text = "0 ms";

                    TryAutoLoadProfilesForActiveDevices();
                    UpdateCalibrationDisplayForCurrentSlot();

                    lblStatus.Text = $"Status: Cleared saved calibration profile for Slot {_calActiveSlot}.";
                    lblStatus.Foreground = Brushes.LightGreen;
                }
                else
                {
                    if (_calActiveSlot == 1 && sliderDelay1 != null) { sliderDelay1.Value = 0; delay1Ms = 0; }
                    else if (_calActiveSlot == 2 && sliderDelay2 != null) { sliderDelay2.Value = 0; delay2Ms = 0; }
                    else if (_calActiveSlot == 3 && sliderDelay3 != null) { sliderDelay3.Value = 0; delay3Ms = 0; }

                    _recordedRawTaps.Clear();
                    _nudgeOffsetMs = 0;
                    if (lblFineTuneVal != null) lblFineTuneVal.Text = "0 ms";
                    UpdateCalibrationDisplayForCurrentSlot();
                }
            }

            if (btnClearSaved != null)
            {
                btnClearSaved.Content = "Cleared! ✓";
                await Task.Delay(1500);
                if (btnClearSaved != null)
                {
                    btnClearSaved.Content = "🗑️ Clear";
                }
            }
        }

        #endregion

        private async void BtnCopyOffset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText($"-{globalOffsetMs} ms");
                btnCopyOffset.Content = "Copied! ✓";
                await Task.Delay(1500);
                btnCopyOffset.Content = "Copy Offset";
            }
            catch { }
        }

        #region Streaming Engine

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (isStreaming) return;
            if (isVisualCalibrating)
            {
                CloseSidePanel();
            }

            var dev1 = GetDeviceFromCombo(comboDevice1);
            var dev2 = GetDeviceFromCombo(comboDevice2);
            var dev3 = GetDeviceFromCombo(comboDevice3);

            if (dev1 == null && dev2 == null && dev3 == null)
            {
                MessageBox.Show("Please select at least one active output.");
                return;
            }

            int wirelessCount = new[] { dev1, dev2, dev3 }.Count(d => d != null && IsWirelessDevice(d));
            if (wirelessCount > 2)
            {
                MessageBox.Show("Maximum of 2 wireless Bluetooth devices allowed simultaneously to prevent bandwidth bottleneck.");
                return;
            }

            using (var tempEnumerator = new MMDeviceEnumerator())
            {
                var currentDefault = tempEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (currentDefault != null)
                {
                    if (dev1?.ID == currentDefault.ID || dev2?.ID == currentDefault.ID || dev3?.ID == currentDefault.ID)
                    {
                        if (!IsVbCable(currentDefault))
                        {
                            var result = MessageBox.Show(
                                $"Feedback Routing Conflict!\n\nWindows is currently outputting default audio to '{currentDefault.FriendlyName}', which you also selected as a SoundPair output.\n\nWould you like SoundPair to switch the Windows default output to VB-Cable now?",
                                "Audio Routing Configuration",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question);

                            if (result == MessageBoxResult.Yes)
                            {
                                SetVBCableAsSystemDefault();
                            }
                            return;
                        }
                        else
                        {
                            MessageBox.Show("Feedback Loop Detected!\n\nPlease do not select 'VB-Cable' as an output device in SoundPair. Select your physical headphones/speakers instead.", "Routing Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                    }
                }
            }

            try
            {
                isStreaming = true;
                btnStart.IsEnabled = false;

                lblStatus.Text = "Status: Waking up Bluetooth radios...";
                lblStatus.Foreground = Brushes.Orange;
                await ExecuteBluetoothHardwareHandshake(dev1, dev2, dev3);

                lblStatus.Text = "Status: Initializing streams...";
                activeId1 = dev1?.ID;
                activeId2 = dev2?.ID;
                activeId3 = dev3?.ID;

                // Loopback capture from default output (VB-Cable)
                capture = new WasapiRecorderBuilder().WithLoopbackCapture().Build();

                lock (_bufferLock)
                {
                    if (dev1 != null) buffer1 = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true };
                    if (dev2 != null) buffer2 = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true };
                    if (dev3 != null) buffer3 = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true };

                    ApplyDelays();
                }

                byte[] transferBuffer = new byte[8192];

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

                            buffer.CopyTo(transferBuffer);

                            // Safe concurrent write to active providers without dropping frames
                            lock (_bufferLock)
                            {
                                buffer1?.AddSamples(transferBuffer, 0, buffer.Length);
                                buffer2?.AddSamples(transferBuffer, 0, buffer.Length);
                                buffer3?.AddSamples(transferBuffer, 0, buffer.Length);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioCapture Error] {ex.Message}");
                    }
                };

                // Pipeline creation with sample-rate adaptation
                if (dev1 != null && buffer1 != null)
                {
                    sampleChannel1 = new SampleChannel(buffer1, true);
                    ISampleProvider prov1 = sampleChannel1;
                    int targetRate = GetDeviceMixFormat(dev1).SampleRate;
                    if (prov1.WaveFormat.SampleRate != targetRate)
                    {
                        prov1 = new WdlResamplingSampleProvider(prov1, targetRate);
                    }
                    out1 = new WasapiPlayerBuilder().WithDevice(dev1).Build();
                    out1.Init(prov1);
                }

                if (dev2 != null && buffer2 != null)
                {
                    sampleChannel2 = new SampleChannel(buffer2, true);
                    ISampleProvider prov2 = sampleChannel2;
                    int targetRate = GetDeviceMixFormat(dev2).SampleRate;
                    if (prov2.WaveFormat.SampleRate != targetRate)
                    {
                        prov2 = new WdlResamplingSampleProvider(prov2, targetRate);
                    }
                    out2 = new WasapiPlayerBuilder().WithDevice(dev2).Build();
                    out2.Init(prov2);
                }

                if (dev3 != null && buffer3 != null)
                {
                    sampleChannel3 = new SampleChannel(buffer3, true);
                    ISampleProvider prov3 = sampleChannel3;
                    int targetRate = GetDeviceMixFormat(dev3).SampleRate;
                    if (prov3.WaveFormat.SampleRate != targetRate)
                    {
                        prov3 = new WdlResamplingSampleProvider(prov3, targetRate);
                    }
                    out3 = new WasapiPlayerBuilder().WithDevice(dev3).Build();
                    out3.Init(prov3);
                }

                // Start capture and playback immediately together (Zero artificial delay)
                capture.StartRecording();
                out1?.Play();
                out2?.Play();
                out3?.Play();

                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    defaultRenderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                    if (defaultRenderDevice != null)
                    {
                        try
                        {
                            cachedSysVol = defaultRenderDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                            defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification += OnSystemVolumeNotification;
                        }
                        catch
                        {
                            cachedSysVol = 1.0f;
                        }
                    }
                }
                catch { }

                ApplySystemAndAppVolumes();

                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;
                btnStop.Background = new SolidColorBrush(Color.FromRgb(210, 60, 80));
                btnStop.Foreground = Brushes.White;

                comboDevice1.IsEnabled = comboDevice2.IsEnabled = comboDevice3.IsEnabled = false;
                lblStatus.Text = "Status: Streaming live audio";
                lblStatus.Foreground = Brushes.LightGreen;
            }
            catch (Exception ex)
            {
                isStreaming = false;
                MessageBox.Show($"Stream failed: {ex.Message}");
                BtnStop_Click(null, null);
            }
        }

        private void OnSystemVolumeNotification(AudioVolumeNotificationData data)
        {
            cachedSysVol = data.MasterVolume;
            ApplySystemAndAppVolumes();
        }

        private void SliderVol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblVol1Val == null || lblVol2Val == null || lblVol3Val == null) return;

            if (sender == sliderVol1 && !_isSyncingVolume1)
            {
                lblVol1Val.Text = $"{(int)sliderVol1.Value}%";
                if (boundDevice1 != null)
                {
                    try
                    {
                        float clampedVol = Math.Max(0.0f, Math.Min(1.0f, (float)(sliderVol1.Value / 100.0)));
                        if (Math.Abs(boundDevice1.AudioEndpointVolume.MasterVolumeLevelScalar - clampedVol) > 0.005f)
                            boundDevice1.AudioEndpointVolume.MasterVolumeLevelScalar = clampedVol;
                    }
                    catch { }
                }
            }
            else if (sender == sliderVol2 && !_isSyncingVolume2)
            {
                lblVol2Val.Text = $"{(int)sliderVol2.Value}%";
                if (boundDevice2 != null)
                {
                    try
                    {
                        float clampedVol = Math.Max(0.0f, Math.Min(1.0f, (float)(sliderVol2.Value / 100.0)));
                        if (Math.Abs(boundDevice2.AudioEndpointVolume.MasterVolumeLevelScalar - clampedVol) > 0.005f)
                            boundDevice2.AudioEndpointVolume.MasterVolumeLevelScalar = clampedVol;
                    }
                    catch { }
                }
            }
            else if (sender == sliderVol3 && !_isSyncingVolume3)
            {
                lblVol3Val.Text = $"{(int)sliderVol3.Value}%";
                if (boundDevice3 != null)
                {
                    try
                    {
                        float clampedVol = Math.Max(0.0f, Math.Min(1.0f, (float)(sliderVol3.Value / 100.0)));
                        if (Math.Abs(boundDevice3.AudioEndpointVolume.MasterVolumeLevelScalar - clampedVol) > 0.005f)
                            boundDevice3.AudioEndpointVolume.MasterVolumeLevelScalar = clampedVol;
                    }
                    catch { }
                }
            }
        }

        private void SliderDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblDelay1Val == null || lblDelay2Val == null || lblDelay3Val == null) return;

            if (sender == sliderDelay1)
            {
                delay1Ms = (int)sliderDelay1.Value;
                lblDelay1Val.Text = $"{delay1Ms} ms";
            }
            else if (sender == sliderDelay2)
            {
                delay2Ms = (int)sliderDelay2.Value;
                lblDelay2Val.Text = $"{delay2Ms} ms";
            }
            else if (sender == sliderDelay3)
            {
                delay3Ms = (int)sliderDelay3.Value;
                lblDelay3Val.Text = $"{delay3Ms} ms";
            }

            if (!isStreaming) return;

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

        private void LblDelay_Reset_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender == lblDelay1Val)
            {
                sliderDelay1.Value = 0;
            }
            else if (sender == lblDelay2Val)
            {
                sliderDelay2.Value = 0;
            }
            else if (sender == lblDelay3Val)
            {
                sliderDelay3.Value = 0;
            }
        }

        private void ApplySystemAndAppVolumes()
        {
            if (sampleChannel1 != null) sampleChannel1.Volume = cachedSysVol;
            if (sampleChannel2 != null) sampleChannel2.Volume = cachedSysVol;
            if (sampleChannel3 != null) sampleChannel3.Volume = cachedSysVol;
        }

        private void ApplyDelays()
        {
            if (capture == null) return;
            int blockAlign = capture.WaveFormat.BlockAlign;
            int bps = capture.WaveFormat.AverageBytesPerSecond / 1000;

            ApplyBufferDelay(buffer1, BaseDelayMs + delay1Ms, bps, blockAlign);
            ApplyBufferDelay(buffer2, BaseDelayMs + delay2Ms, bps, blockAlign);
            ApplyBufferDelay(buffer3, BaseDelayMs + delay3Ms, bps, blockAlign);
        }

        private void UpdateSingleBufferDelay(BufferedWaveProvider? buf, int delayMs)
        {
            if (buf == null || capture == null) return;
            lock (_bufferLock)
            {
                int blockAlign = capture.WaveFormat.BlockAlign;
                int bps = capture.WaveFormat.AverageBytesPerSecond / 1000;
                ApplyBufferDelay(buf, BaseDelayMs + delayMs, bps, blockAlign);
            }
        }

        private void ApplyBufferDelay(BufferedWaveProvider? buf, int delayMs, int bps, int blockAlign)
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
            if (defaultRenderDevice != null)
            {
                try { defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification -= OnSystemVolumeNotification; } catch { }
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

            isStreaming = false;
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            btnStop.Background = new SolidColorBrush(Color.FromRgb(40, 48, 64));
            btnStop.Foreground = new SolidColorBrush(Color.FromRgb(110, 120, 140));

            comboDevice1.IsEnabled = comboDevice2.IsEnabled = comboDevice3.IsEnabled = true;
            lblStatus.Text = "Status: Stopped";
            lblStatus.Foreground = (Brush)FindResource("TextSec");
        }

        #endregion

        protected override void OnClosing(CancelEventArgs e)
        {
            debounceTimer?.Stop();
            debounceTimer = null;
            _delayDebounceTimer?.Stop();
            _delayDebounceTimer = null;

            var helper = new WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                HwndSource.FromHwnd(helper.Handle)?.RemoveHook(HwndHook);
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            CloseSidePanel();
            UnbindAllDevices();

            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
                notifyIcon.ContextMenuStrip?.Dispose();
                notifyIcon.Dispose();
                notifyIcon = null;
            }

            if (didChangeDefaultDevice && !string.IsNullOrEmpty(originalDefaultDeviceId))
            {
                SetSystemDefaultAudioDevice(originalDefaultDeviceId);
            }

            BtnStop_Click(null, null);
            foreach (var d in _cachedDevices)
            {
                try { d.Dispose(); } catch { }
            }
            _cachedDevices.Clear();

            base.OnClosed(e);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
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
