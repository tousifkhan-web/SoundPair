// SoundPair Video Sync - Popup Script

function updatePopupUI() {
  chrome.runtime.sendMessage({ type: "GET_SOUNDPAIR_STATE" }, (state) => {
    if (!state) return;

    const appStatus = document.getElementById("appStatus");
    const streamState = document.getElementById("streamState");
    const offsetVal = document.getElementById("offsetVal");

    if (state.connected) {
      appStatus.textContent = `Connected (${state.port})`;
      appStatus.className = "badge badge-online";
    } else {
      appStatus.textContent = "Disconnected";
      appStatus.className = "badge badge-offline";
    }

    if (state.isStreaming) {
      streamState.textContent = "Streaming 🟢";
      streamState.style.color = "#2ECC71";
    } else {
      streamState.textContent = "Idle";
      streamState.style.color = "#E0E0E0";
    }

    offsetVal.textContent = state.offsetMs > 0 ? `+${state.offsetMs} ms (Delays Video)` : "0 ms";
  });
}

document.addEventListener("DOMContentLoaded", () => {
  updatePopupUI();
  setInterval(updatePopupUI, 1000);
});
