using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
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
        private DispatcherTimer devicePollTimer;

        private int delay2Ms = 0;
        private int delay3Ms = 0;
        private const int BaseDelayMs = 200; // Baseline pre-buffer allowing negative relative delays

        private const int WM_DEVICECHANGE = 0x0219;

        public MainWindow()
        {
            InitializeComponent();
            LoadAudioDevices();

            devicePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            devicePollTimer.Tick += (s, e) =>
            {
                if (!isStreaming)
                {
                    RefreshDevicesIfNeeded();
                }
            };
            devicePollTimer.Start();
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
                if (!isStreaming)
                {
                    LoadAudioDevices();
                }
            }
            return IntPtr.Zero;
        }

        private void RefreshDevicesIfNeeded()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var currentDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                if (devices == null || currentDevices.Count != devices.Count)
                {
                    LoadAudioDevices();
                    return;
                }

                for (int i = 0; i < currentDevices.Count; i++)
                {
                    if (currentDevices[i].FriendlyName != devices[i].FriendlyName)
                    {
                        LoadAudioDevices();
                        break;
                    }
                }
            }
            catch { }
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

                SelectDeviceByName(comboDevice1, selectedName1, devices, devices.Count > 0 ? 1 : 0);
                SelectDeviceByName(comboDevice2, selectedName2, devices, 0);
                SelectDeviceByName(comboDevice3, selectedName3, devices, 0);

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

            string zipUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip";
            string tempFolder = Path.Combine(Path.GetTempPath(), "SoundPair_VBCable");
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
                ZipFile.ExtractToDirectory(zipPath, tempFolder);

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

            try
            {
                isStreaming = true;
                btnStart.IsEnabled = false;
                lblStatus.Text = "Status: Initializing streams...";
                lblStatus.Foreground = Brushes.Orange;

                var dummyFormat = new WaveFormat(44100, 16, 2);
                var dummyProv = new BufferedWaveProvider(dummyFormat);
                byte[] silence = new byte[dummyFormat.AverageBytesPerSecond / 2];
                dummyProv.AddSamples(silence, 0, silence.Length);

                if (dev1 != null) { using var t = new WasapiPlayerBuilder().WithDevice(dev1).Build(); t.Init(dummyProv); t.Play(); }
                if (dev2 != null) { using var t = new WasapiPlayerBuilder().WithDevice(dev2).Build(); t.Init(dummyProv); t.Play(); }
                if (dev3 != null) { using var t = new WasapiPlayerBuilder().WithDevice(dev3).Build(); t.Init(dummyProv); t.Play(); }
                await Task.Delay(300);

                capture = new WasapiRecorderBuilder().WithLoopbackCapture().Build();

                if (dev1 != null) buffer1 = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true };
                if (dev2 != null) buffer2 = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true };
                if (dev3 != null) buffer3 = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true };

                ApplyDelays();

                var b1 = buffer1; var b2 = buffer2; var b3 = buffer3;
                capture.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
                {
                    if (buffer.Length > 0)
                    {
                        byte[] rawData = buffer.ToArray();
                        b1?.AddSamples(rawData, 0, rawData.Length);
                        b2?.AddSamples(rawData, 0, rawData.Length);
                        b3?.AddSamples(rawData, 0, rawData.Length);
                    }
                };

                if (buffer1 != null) sampleChannel1 = new SampleChannel(buffer1, true);
                if (buffer2 != null) sampleChannel2 = new SampleChannel(buffer2, true);
                if (buffer3 != null) sampleChannel3 = new SampleChannel(buffer3, true);

                if (dev1 != null && sampleChannel1 != null) { out1 = new WasapiPlayerBuilder().WithDevice(dev1).Build(); out1.Init(sampleChannel1); }
                if (dev2 != null && sampleChannel2 != null) { out2 = new WasapiPlayerBuilder().WithDevice(dev2).Build(); out2.Init(sampleChannel2); }
                if (dev3 != null && sampleChannel3 != null) { out3 = new WasapiPlayerBuilder().WithDevice(dev3).Build(); out3.Init(sampleChannel3); }

                capture.StartRecording();
                await Task.Delay(100);

                out1?.Play(); out2?.Play(); out3?.Play();

                using var enumerator = new MMDeviceEnumerator();
                defaultRenderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification += OnSystemVolumeNotification;

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
            
            // Format labels with explicit + or - signs for clarity
            lblDelay2Val.Text = (delay2Ms > 0 ? $"+{delay2Ms}" : $"{delay2Ms}") + " ms";
            lblDelay3Val.Text = (delay3Ms > 0 ? $"+{delay3Ms}" : $"{delay3Ms}") + " ms";
            
            ApplyDelays();
        }

        private void ApplySystemAndAppVolumes()
        {
            float sysVol = 1.0f;
            try { if (defaultRenderDevice != null) sysVol = defaultRenderDevice.AudioEndpointVolume.MasterVolumeLevelScalar; } catch { }

            if (sampleChannel1 != null) sampleChannel1.Volume = sysVol * ((float)sliderVol1.Value / 100f);
            if (sampleChannel2 != null) sampleChannel2.Volume = sysVol * ((float)sliderVol2.Value / 100f);
            if (sampleChannel3 != null) sampleChannel3.Volume = sysVol * ((float)sliderVol3.Value / 100f);
        }

        private void ApplyDelays()
        {
            if (capture == null) return;
            int blockAlign = capture.WaveFormat.BlockAlign;
            int bps = capture.WaveFormat.AverageBytesPerSecond / 1000;

            // Apply base delay to slot 1, and offset slots 2 & 3 relative to it
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
                if (bytesNeeded > 0)
                {
                    buf.AddSamples(new byte[bytesNeeded], 0, bytesNeeded);
                }
            }
        }

        private void BtnStop_Click(object? sender, RoutedEventArgs? e)
        {
            try
            {
                if (defaultRenderDevice != null)
                {
                    defaultRenderDevice.AudioEndpointVolume.OnVolumeNotification -= OnSystemVolumeNotification;
                    defaultRenderDevice.Dispose();
                    defaultRenderDevice = null;
                }

                capture?.StopRecording(); 
                out1?.Stop(); out2?.Stop(); out3?.Stop();

                capture?.Dispose(); capture = null;
                out1?.Dispose(); out1 = null;
                out2?.Dispose(); out2 = null;
                out3?.Dispose(); out3 = null;

                buffer1 = buffer2 = buffer3 = null;
                sampleChannel1 = sampleChannel2 = sampleChannel3 = null;
            }
            catch { }

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
            devicePollTimer?.Stop();
            BtnStop_Click(null, null);
            base.OnClosed(e);
        }
    }
}