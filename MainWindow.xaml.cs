using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
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

        private MMDeviceCollection? devices;
        private MMDevice? defaultRenderDevice;
        private bool isStreaming = false;
        private bool hasCheckedVbCable = false;
        private bool isInitialLoad = true;

        private int delay2Ms = 0;
        private int delay3Ms = 0;
        private const int BaseDelayMs = 10; 
        private const int WM_DEVICECHANGE = 0x0219;

        private float cachedSysVol = 1.0f;
        private string? activeId1, activeId2, activeId3;
        private string? originalDefaultDeviceId = null;

        private System.Windows.Forms.NotifyIcon? notifyIcon;
        private DispatcherTimer? debounceTimer;
        private static readonly byte[] SilenceBuffer = new byte[1048576];

        public MainWindow()
        {
            InitializeComponent();
            InitializeTrayIcon();
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

        private void SaveOriginalDefaultDevice()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var defaultDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                originalDefaultDeviceId = defaultDev.ID;
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
                    if (device.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) || 
                        device.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
                    {
                        SetSystemDefaultAudioDevice(device.ID);
                        break;
                    }
                }
            }
            catch { }
        }

        private void SetSystemDefaultAudioDevice(string deviceId)
        {
            try
            {
                var policyConfig = (IPolicyConfig)new PolicyConfigClient();
                policyConfig.SetDefaultEndpoint(deviceId, Role.Multimedia);
                policyConfig.SetDefaultEndpoint(deviceId, Role.Console);
                policyConfig.SetDefaultEndpoint(deviceId, Role.Communications);
            }
            catch { }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            HwndSource.FromHwnd(helper.Handle)?.AddHook(HwndHook);
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
                        LoadAudioDevices();
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
                var activeEndpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                bool IsAlive(string? id) => id != null && activeEndpoints.Any(d => d.ID == id);

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
            player?.Dispose();
            
            player = null;
            buffer = null;
            channel = null;
            activeId = null;
        }

        private void LoadAudioDevices()
        {
            try
            {
                string? selectedName1 = GetPureDeviceName(comboDevice1.SelectedItem?.ToString());
                string? selectedName2 = GetPureDeviceName(comboDevice2.SelectedItem?.ToString());
                string? selectedName3 = GetPureDeviceName(comboDevice3.SelectedItem?.ToString());

                comboDevice1.Items.Clear();
                comboDevice2.Items.Clear();
                comboDevice3.Items.Clear();

                comboDevice1.Items.Add("-- Disabled / Blank --");
                comboDevice2.Items.Add("-- Disabled / Blank --");
                comboDevice3.Items.Add("-- Disabled / Blank --");

                using var enumerator = new MMDeviceEnumerator();
                
                devices?.Dispose(); 
                devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                
                bool vbCableFound = false;

                for (int i = 0; i < devices.Count; i++)
                {
                    string name = devices[i].FriendlyName;
                    comboDevice1.Items.Add($"[{i}] {name}");
                    comboDevice2.Items.Add($"[{i}] {name}");
                    comboDevice3.Items.Add($"[{i}] {name}");

                    if (name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) || 
                        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
                    {
                        vbCableFound = true;
                    }
                }

                SelectDeviceByName(comboDevice1, selectedName1, devices, isInitialLoad && devices.Count > 0 ? 1 : 0);
                SelectDeviceByName(comboDevice2, selectedName2, devices, 0);
                SelectDeviceByName(comboDevice3, selectedName3, devices, 0);
                
                isInitialLoad = false;

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

                using (var client = new HttpClient())
                {
                    var response = await client.GetByteArrayAsync(zipUrl);
                    await File.WriteAllBytesAsync(zipPath, response);
                }

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

        private void SelectDeviceByName(System.Windows.Controls.ComboBox cb, string? targetName, MMDeviceCollection activeDevices, int defaultIdx)
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

        private bool IsWirelessDevice(MMDevice device)
        {
            return device.FriendlyName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                   device.FriendlyName.Contains("Wireless", StringComparison.OrdinalIgnoreCase);
        }

        private MMDevice? GetDeviceFromCombo(System.Windows.Controls.ComboBox cb)
        {
            if (devices == null || cb.SelectedIndex <= 0 || (cb.SelectedIndex - 1) >= devices.Count) return null;
            return devices[cb.SelectedIndex - 1];
        }

        private async Task ExecuteBluetoothHardwareHandshake(MMDevice? dev1, MMDevice? dev2, MMDevice? dev3)
        {
            var dummyFormat = new WaveFormat(44100, 16, 2);
            var dummyProv = new BufferedWaveProvider(dummyFormat);
            
            byte[] silence = new byte[dummyFormat.AverageBytesPerSecond * 2];
            dummyProv.AddSamples(silence, 0, silence.Length);

            var t1 = dev1 != null ? new WasapiPlayerBuilder().WithDevice(dev1).Build() : null;
            var t2 = dev2 != null ? new WasapiPlayerBuilder().WithDevice(dev2).Build() : null;
            var t3 = dev3 != null ? new WasapiPlayerBuilder().WithDevice(dev3).Build() : null;

            try 
            {
                if (t1 != null) { t1.Init(dummyProv); t1.Play(); }
                if (t2 != null) { t2.Init(dummyProv); t2.Play(); }
                if (t3 != null) { t3.Init(dummyProv); t3.Play(); }

                await Task.Delay(1200);
            }
            finally
            {
                try { t1?.Stop(); } catch { }
                try { t2?.Stop(); } catch { }
                try { t3?.Stop(); } catch { }
                t1?.Dispose();
                t2?.Dispose();
                t3?.Dispose();
            }
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (isStreaming) return;

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
                MessageBox.Show("Maximum of 2 wireless Bluetooth devices allowed simultaneously.");
                return;
            }

            using (var tempEnumerator = new MMDeviceEnumerator())
            {
                var currentDefault = tempEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (currentDefault != null)
                {
                    if (dev1?.ID == currentDefault.ID || dev2?.ID == currentDefault.ID || dev3?.ID == currentDefault.ID)
                    {
                        MessageBox.Show("Feedback Loop Detected!\n\nYou are routing audio back into the Default System Device. This causes a metallic echoing loop.\n\nPlease set your Windows taskbar audio output to 'VB-Cable', then select your real headsets in SoundPair.", "Routing Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
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

                capture = new WasapiRecorderBuilder().WithLoopbackCapture().Build();

                if (dev1 != null) buffer1 = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true };
                if (dev2 != null) buffer2 = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true };
                if (dev3 != null) buffer3 = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true };

                ApplyDelays();

                byte[] transferBuffer = new byte[8192]; 
                int bps = capture.WaveFormat.AverageBytesPerSecond / 1000;
                int driftTolerance = bps * 40; 

                capture.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
                {
                    if (buffer.Length > 0)
                    {
                        if (transferBuffer.Length < buffer.Length) 
                        {
                            transferBuffer = new byte[buffer.Length];
                        }
                        
                        buffer.CopyTo(transferBuffer); 

                        bool needsResync = false;

                        int target1 = Math.Max(0, BaseDelayMs * bps);
                        int target2 = Math.Max(0, (BaseDelayMs + delay2Ms) * bps);
                        int target3 = Math.Max(0, (BaseDelayMs + delay3Ms) * bps);

                        if (buffer1 != null && Math.Abs(buffer1.BufferedBytes - target1) > driftTolerance) needsResync = true;
                        if (buffer2 != null && Math.Abs(buffer2.BufferedBytes - target2) > driftTolerance) needsResync = true;
                        if (buffer3 != null && Math.Abs(buffer3.BufferedBytes - target3) > driftTolerance) needsResync = true;

                        if (needsResync)
                        {
                            ApplyDelays();
                        }
                        else
                        {
                            buffer1?.AddSamples(transferBuffer, 0, buffer.Length);
                            buffer2?.AddSamples(transferBuffer, 0, buffer.Length);
                            buffer3?.AddSamples(transferBuffer, 0, buffer.Length);
                        }
                    }
                };

                if (buffer1 != null) sampleChannel1 = new SampleChannel(buffer1, true);
                if (buffer2 != null) sampleChannel2 = new SampleChannel(buffer2, true);
                if (buffer3 != null) sampleChannel3 = new SampleChannel(buffer3, true);

                if (dev1 != null && sampleChannel1 != null) { out1 = new WasapiPlayerBuilder().WithDevice(dev1).Build(); out1.Init(sampleChannel1); }
                if (dev2 != null && sampleChannel2 != null) { out2 = new WasapiPlayerBuilder().WithDevice(dev2).Build(); out2.Init(sampleChannel2); }
                if (dev3 != null && sampleChannel3 != null) { out3 = new WasapiPlayerBuilder().WithDevice(dev3).Build(); out3.Init(sampleChannel3); }

                out1?.Play(); 
                out2?.Play(); 
                out3?.Play();

                await Task.Delay(50);
                capture.StartRecording();

                try 
                {
                    using var enumerator = new MMDeviceEnumerator();
                    defaultRenderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    
                    if (defaultRenderDevice != null)
                    {
                        cachedSysVol = defaultRenderDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                        defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification += OnSystemVolumeNotification;
                    }
                } 
                catch { }

                ApplySystemAndAppVolumes();

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
            Dispatcher.Invoke(() => ApplySystemAndAppVolumes());
        }

        private void SliderVol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblVol1Val == null || lblVol2Val == null || lblVol3Val == null) return; 
            
            lblVol1Val.Text = $"{(int)sliderVol1.Value}%";
            lblVol2Val.Text = $"{(int)sliderVol2.Value}%";
            lblVol3Val.Text = $"{(int)sliderVol3.Value}%";
            
            ApplySystemAndAppVolumes();
        }

        private void SliderDelay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblDelay2Val == null || lblDelay3Val == null) return;
            
            delay2Ms = (int)sliderDelay2.Value;
            delay3Ms = (int)sliderDelay3.Value;
            
            lblDelay2Val.Text = $"{delay2Ms} ms";
            lblDelay3Val.Text = $"{delay3Ms} ms";
            
            ApplyDelays();
        }

        private void LblDelay_Reset_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender == lblDelay2Val)
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
            if (sampleChannel1 != null) sampleChannel1.Volume = cachedSysVol * ((float)sliderVol1.Value / 100f);
            if (sampleChannel2 != null) sampleChannel2.Volume = cachedSysVol * ((float)sliderVol2.Value / 100f);
            if (sampleChannel3 != null) sampleChannel3.Volume = cachedSysVol * ((float)sliderVol3.Value / 100f);
        }

        private void ApplyDelays()
        {
            if (capture == null) return;
            int blockAlign = capture.WaveFormat.BlockAlign;
            int bps = capture.WaveFormat.AverageBytesPerSecond / 1000;

            ApplyBufferDelay(buffer1, BaseDelayMs, bps, blockAlign);
            ApplyBufferDelay(buffer2, BaseDelayMs + delay2Ms, bps, blockAlign);
            ApplyBufferDelay(buffer3, BaseDelayMs + delay3Ms, bps, blockAlign);
        }

        private void ApplyBufferDelay(BufferedWaveProvider? buf, int delayMs, int bps, int blockAlign)
        {
            if (buf == null) return;
            buf.ClearBuffer();
            if (delayMs > 0)
            {
                int bytesNeeded = bps * delayMs;
                bytesNeeded -= bytesNeeded % blockAlign;
                
                if (bytesNeeded > 0 && bytesNeeded <= SilenceBuffer.Length)
                {
                    buf.AddSamples(SilenceBuffer, 0, bytesNeeded);
                }
            }
        }

        private void BtnStop_Click(object? sender, RoutedEventArgs? e)
        {
            if (defaultRenderDevice != null)
            {
                defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification -= OnSystemVolumeNotification;
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

            buffer1 = buffer2 = buffer3 = null;
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

        protected override void OnClosed(EventArgs e)
        {
            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
                notifyIcon.ContextMenuStrip?.Dispose();
                notifyIcon.Dispose();
                notifyIcon = null;
            }

            if (!string.IsNullOrEmpty(originalDefaultDeviceId))
            {
                SetSystemDefaultAudioDevice(originalDefaultDeviceId);
            }

            var helper = new WindowInteropHelper(this);
            HwndSource.FromHwnd(helper.Handle)?.RemoveHook(HwndHook);

            BtnStop_Click(null, null);
            devices?.Dispose(); 

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