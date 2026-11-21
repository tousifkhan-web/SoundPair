// SoundPair Video Sync - Background Service Worker

let soundPairState = {
  connected: false,
  isStreaming: false,
  offsetMs: 0,
  activePlayer: "None",
  port: 48888,
  lastCheck: 0
};

let lastBroadcastState = "";
const PORTS_TO_CHECK = [48888, 48889];
let pollTimerId = null;

async function pollSoundPair() {
  let found = false;

  for (const port of PORTS_TO_CHECK) {
    try {
      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), 800);

      const res = await fetch(`http://127.0.0.1:${port}/offset`, {
        signal: controller.signal
      });
      clearTimeout(timeoutId);

      if (res.ok) {
        const data = await res.json();
        soundPairState = {
          connected: true,
          isStreaming: !!data.isStreaming,
          offsetMs: data.offsetMs || 0,
          activePlayer: data.activePlayer || "None",
          port: port,
          lastCheck: Date.now()
        };
        found = true;
        break;
      }
    } catch (e) {
      // Port unreachable, check next
    }
  }

  if (!found) {
    soundPairState.connected = false;
    soundPairState.isStreaming = false;
  }

  // Only broadcast to open tabs if state actually changed to save IPC overhead
  const stateSignature = `${soundPairState.connected}_${soundPairState.isStreaming}_${soundPairState.offsetMs}`;
  if (stateSignature !== lastBroadcastState) {
    lastBroadcastState = stateSignature;
    try {
      const tabs = await chrome.tabs.query({});
      for (const tab of tabs) {
        if (tab.id && tab.url && (tab.url.startsWith("http://") || tab.url.startsWith("https://"))) {
          chrome.tabs.sendMessage(tab.id, {
            type: "SOUNDPAIR_STATE_UPDATE",
            state: soundPairState
          }).catch(() => {
            // Tab may not have content script loaded yet
          });
        }
      }
    } catch (err) {
      // Ignore tab query errors
    }
  }

  // Adaptive scheduling: poll faster (2s) when actively streaming, back off (5s) when disconnected/idle
  const nextDelay = (soundPairState.connected && soundPairState.isStreaming) ? 2000 : 5000;
  clearTimeout(pollTimerId);
  pollTimerId = setTimeout(pollSoundPair, nextDelay);
}

// Initial poll
pollSoundPair();

// Listen for messages from popup or content scripts
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.type === "GET_SOUNDPAIR_STATE") {
    sendResponse(soundPairState);
  }
  return true;
});
