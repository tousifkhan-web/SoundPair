# SoundPair — Browser Extension

The official companion extension for the **SoundPair** desktop application. Automatically synchronizes HTML5 video playback across streaming sites (YouTube, Netflix, Prime Video, Disney+, Twitch, etc.) with SoundPair's audio delay offset to eliminate Bluetooth audio lag.

---

## 🚀 How to Install

### A. Google Chrome, Microsoft Edge & Brave

1. Open your browser and navigate to:
   - **Chrome / Brave**: `chrome://extensions`
   - **Edge**: `edge://extensions`
2. Toggle **Developer mode** to **ON** (top-right corner in Chrome/Brave, left sidebar in Edge).
3. Click **Load unpacked** (top-left).
4. Select this folder (`browser_extension`).
5. Pin **SoundPair** to your toolbar!

---

### B. Mozilla Firefox

1. Install directly from the **[Firefox Add-ons Store (AMO)](https://addons.mozilla.org/firefox/addon/soundpair/)** with 1 click.
2. *Or for local / developer testing*:
   - Open Firefox and go to `about:debugging#/runtime/this-firefox`.
   - Click **Load Temporary Add-on...**
   - Select `soundpair.xpi` (or `manifest.json`) in this directory.

---

## 💡 How It Works

1. When you start streaming audio in the SoundPair desktop app, its built-in local sync server broadcasts your calibrated delay offset via `http://127.0.0.1:48888`.
2. The SoundPair extension connects locally in the background.
3. As you watch any HTML5 video, the extension dynamically holds back video frames by the exact millisecond delay so audio and visuals remain in perfect lip-sync.
4. **100% Private**: Zero cloud servers, zero telemetry, zero tracking. All communication is strictly local to your machine.
