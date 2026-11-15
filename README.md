# SoundPair

A modern, high-performance C# WPF desktop application designed to route audio via **VB-Cable** to **three audio devices simultaneously** with millisecond-precision delay synchronization, independent volume controls, visual calibration, and real-time Windows master volume linking.

---

## 📷 App Preview

![SoundPair Screenshot](screenshot1.png) ![SoundPair Screenshot 2](screenshot2.png)

---

## 🚀 Setup & Quick Start Guide

Follow these simple steps to get SoundPair up and running from scratch:

### Step 1: Install the Virtual Audio Driver

SoundPair uses the **VB-Cable Virtual Audio Driver** to capture and isolate clean media audio without recording notification pings or background noise.

- **Automatic Prompt:** When you launch SoundPair for the first time, if VB-Cable is missing, the application will automatically offer to download and launch the installer for you.
- **Manual Installation:** Alternatively, download and install VB-Cable manually from the official [VB-Audio website](https://vb-audio.com/Cable/).
- **Restart your PC** after installation so Windows registers the driver properly.

### Step 2: Configure Windows Audio Output

SoundPair handles audio routing smoothly:

- **Automatic Configuration:** When started, SoundPair automatically sets **CABLE Input (VB-Audio Virtual Cable)** as your active Windows default playback device and saves your previous default output.
- **Automatic Restoration:** When you exit SoundPair, it cleanly restores your original Windows default playback device.
- **Manual Check (Optional):** If needed, you can verify in **Windows Sound Settings** that `CABLE Input (VB-Audio Virtual Cable)` is selected as the default output device.

### Step 3: Connect & Select Your Audio Devices

1. Turn on and connect your audio outputs (Bluetooth headsets, wired headphones, speakers, or USB DACs).
2. SoundPair features dynamic hardware detection and will automatically populate your connected outputs in the dropdown menus:
   - **Device Slot 1 (Primary)**
   - **Device Slot 2 (Optional / Adjustable)**
   - **Device Slot 3 (Optional / Adjustable)**
3. Choose the audio devices you want to stream to (select 1, 2, or all 3 slots).

### Step 4: Calibrate & Fine-Tune Audio Delays

- **Visual Calibration Studio (Ball Drop & Spacebar Sync):** Click **🎯 Visual Calibration Studio** (or the **🎯 Cal** button beside any device slot) to open the calibration drawer. Put on your headset, watch the ball drop down the chute, and tap the **[Spacebar]** the instant you hear the audio click. SoundPair averages your tap responses, filters statistical outliers, and automatically balances delay offsets across all active outputs.
- **Persistent Device Profiles:** Calibrated delays are automatically saved to `device_profiles.json`. When you reconnect or select the same device in the future, SoundPair instantly recalls your calibration profile.
- **Manual Delay Offset (0–300 ms):** Use the delay sliders for each slot to manually nudge or fine-tune synchronization. Click on any delay value label (e.g. `0 ms`) to instantly reset it back to 0 ms.
- **Media Player Lip-Sync Offset:** When balancing outputs requires buffering, SoundPair calculates the exact audio-to-video delay compensation for video players (VLC, PotPlayer, MPC-HC) with one-click clipboard copying.
- **Independent Volume Controls:** Adjust the dedicated volume sliders (0–100%) for each slot independently, or let them synchronize in real-time with your Windows master volume mixer and keyboard media keys.

### Step 5: Start Streaming

Click the **Start Streaming** button. SoundPair executes a fast Bluetooth radio warmup handshake to prevent cold-start desyncs, establishes low-latency WASAPI loopback streams, and routes audio to all selected devices in perfect synchronization!

---

## ✨ Key Features

- **Tri-Device Simultaneous Streaming:** Route Windows audio to up to 3 separate playback outputs (wired, USB, or Bluetooth) at the same time.
- **Interactive Visual Calibration Studio:** Visual ball-drop animation with synchronized audio pulses and spacebar reaction timing. Eliminates unreliable microphone acoustic chirps and room noise interference.
- **Persistent Device Profiles:** Automatically saves device latency profiles (`device_profiles.json`) and auto-loads balanced delay configurations whenever recognized devices are selected.
- **Auto-Balanced Multi-Device Delay:** Intelligently identifies the slowest audio device, computes the required buffer padding for faster devices, and adjusts delay sliders automatically.
- **Manual Millisecond Delay Sliders (0–300 ms):** Ultra-fine 0–300 ms adjustment per slot with a one-click reset to eliminate any echo or drift between disparate hardware models.
- **Media Player Lip-Sync Offset:** Computes and displays the exact audio-to-video offset for VLC (`J`/`K`), PotPlayer (`Shift + >`/`<`), and MPC-HC with one-click clipboard copying.
- **Dynamic Sample-Rate Conversion (WDL Resampling):** Integrated NAudio `WdlResamplingSampleProvider` automatically converts and matches sample rates (e.g., 44.1 kHz, 48 kHz, 96 kHz) across mixed hardware without audio degradation or pitch glitches.
- **Independent & Synchronized Volume Controls:** Dedicated volume percentage sliders for Device Slots 1, 2, and 3, linked bidirectionally with Windows endpoint volume notifications in real-time.
- **Automated Default Device Management:** Automatically sets VB-Cable as the Windows default audio output on startup and seamlessly restores your original default audio device on exit.
- **Feedback Loop & Routing Conflict Protection:** Detects if an output slot is mistakenly assigned to VB-Cable or the active Windows default endpoint, prompting the user and preventing dangerous audio feedback loops.
- **Dynamic Hardware Detection:** Actively listens for Windows device arrival and removal events (`WM_DEVICECHANGE`) to refresh device lists dynamically without requiring an app restart.
- **Bluetooth Cold-Start Warmup:** Runs a hardware handshake prior to streaming to wake up dormant Bluetooth radios, eliminating first-launch desync and buffer underruns.
- **Minimize to System Tray:** Seamlessly minimizes to the Windows notification tray area with a right-click context menu (Open / Exit) to keep your workspace clutter-free.
- **Single-Instance Enforcement:** Uses a system-wide named Mutex to ensure only one streaming instance runs at a time, preventing port and endpoint locks.
- **Modern Glassmorphic Dark UI:** Sleek dark-mode aesthetic featuring deep navy containers, accent gradients, custom rounded cards, and native Windows 10/11 dark title bar support (`DwmSetWindowAttribute`).

---

## 🛠️ Tech Stack & Requirements

- **Language:** C# 12 / .NET 10 (Windows-specific platform support)
- **UI Framework:** XAML & WPF (Windows Presentation Foundation)
- **Audio Architecture:** NAudio 3.1.0 (WASAPI Loopback Capture & Low-Latency WasapiPlayer)
- **OS:** Windows 10 / Windows 11 (64-bit)
- **Driver:** VB-Audio Virtual Cable

---

## 📦 How to Build and Publish

SoundPair includes a convenient build script (`Build.bat`) in the project root that creates both lightweight and standalone single-file executables:

```cmd
Build.bat
```

Or you can build via the .NET CLI:

### 1. Lightweight Build (Framework-Dependent)

Produces a compact single-file executable. Requires the target machine to have the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed:

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:AssemblyName=Soundpair_lightweight -o ./publish/light
```

### 2. Standalone Build (Self-Contained)

Bundles the complete .NET 10 runtime, native dependencies, and compression into a single self-extracting executable. Does not require any pre-installed .NET runtimes:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:AssemblyName=SoundPair_standalone -o ./publish/standalone -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```
