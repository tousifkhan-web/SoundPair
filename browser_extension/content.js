// SoundPair Video Sync - Content Script

(function () {
  let currentState = {
    connected: false,
    isStreaming: false,
    offsetMs: 0
  };

  const activeSyncWrappers = new Map();

  // Listen for updates from background service worker
  chrome.runtime.onMessage.addListener((message) => {
    if (message.type === "SOUNDPAIR_STATE_UPDATE") {
      currentState = message.state;
      applySyncToAllVideos();
    }
  });

  // Request initial state on load
  chrome.runtime.sendMessage({ type: "GET_SOUNDPAIR_STATE" }, (state) => {
    if (state) {
      currentState = state;
      applySyncToAllVideos();
    }
  });

  function applySyncToAllVideos() {
    const videos = document.querySelectorAll("video");
    videos.forEach((video) => {
      let wrapper = activeSyncWrappers.get(video);
      if (!wrapper) {
        wrapper = setupVideoSync(video);
        activeSyncWrappers.set(video, wrapper);
      }
      wrapper.updateState(currentState);
    });
  }

  function setupVideoSync(video) {
    let canvas = null;
    let ctx = null;
    let animFrameId = null;
    const frameBuffer = [];
    let isDelayActive = false;

    function ensureCanvas() {
      if (canvas && document.body.contains(canvas)) return;

      canvas = document.createElement("canvas");
      canvas.className = "soundpair-sync-canvas";
      canvas.style.position = "absolute";
      canvas.style.pointerEvents = "none";
      canvas.style.zIndex = "10";
      canvas.style.display = "none";

      if (video.parentElement) {
        const parentPos = window.getComputedStyle(video.parentElement).position;
        if (parentPos === "static") {
          video.parentElement.style.position = "relative";
        }
        video.parentElement.appendChild(canvas);
      }
      ctx = canvas.getContext("2d", { alpha: false });
    }

    function syncCanvasBounds() {
      if (!canvas || !video) return;
      canvas.style.left = video.offsetLeft + "px";
      canvas.style.top = video.offsetTop + "px";
      canvas.style.width = video.clientWidth + "px";
      canvas.style.height = video.clientHeight + "px";

      if (canvas.width !== video.videoWidth && video.videoWidth > 0) {
        canvas.width = video.videoWidth;
        canvas.height = video.videoHeight;
      }
    }

    function renderLoop() {
      if (!isDelayActive || !video || video.paused || video.ended) {
        animFrameId = null;
        return;
      }

      syncCanvasBounds();

      const now = performance.now();
      const targetDelay = currentState.offsetMs;

      // Capture current video frame into buffer if canvas dimensions are valid
      if (video.videoWidth > 0 && video.videoHeight > 0) {
        createImageBitmap(video).then((bitmap) => {
          frameBuffer.push({ bitmap, time: performance.now() });

          // Find the delayed frame
          const renderTime = now - targetDelay;
          let frameToRender = null;

          while (frameBuffer.length > 0 && frameBuffer[0].time <= renderTime) {
            if (frameToRender && frameToRender.bitmap) {
              frameToRender.bitmap.close();
            }
            frameToRender = frameBuffer.shift();
          }

          if (frameToRender && ctx && canvas) {
            ctx.drawImage(frameToRender.bitmap, 0, 0, canvas.width, canvas.height);
            frameToRender.bitmap.close();
          }
        }).catch(() => {
          // Cross-origin or DRM frame capture restriction
        });
      }

      // Keep buffer bounded (max 2 seconds)
      const maxAge = now - 2000;
      while (frameBuffer.length > 0 && frameBuffer[0].time < maxAge) {
        const old = frameBuffer.shift();
        if (old.bitmap) old.bitmap.close();
      }

      animFrameId = requestAnimationFrame(renderLoop);
    }

    function onPlay() {
      if (isDelayActive && !animFrameId && !video.paused && !video.ended) {
        animFrameId = requestAnimationFrame(renderLoop);
      }
    }

    function onPauseOrEnd() {
      if (animFrameId) {
        cancelAnimationFrame(animFrameId);
        animFrameId = null;
      }
    }

    video.addEventListener("play", onPlay);
    video.addEventListener("pause", onPauseOrEnd);
    video.addEventListener("ended", onPauseOrEnd);

    function startDelay() {
      if (isDelayActive) return;
      ensureCanvas();
      if (!canvas) return;

      isDelayActive = true;
      canvas.style.display = "block";
      video.style.opacity = "0.001"; // Keep audio and decoding active while hiding original visuals

      if (!video.paused && !video.ended && !animFrameId) {
        animFrameId = requestAnimationFrame(renderLoop);
      }
    }

    function stopDelay() {
      if (!isDelayActive) return;
      isDelayActive = false;

      if (animFrameId) {
        cancelAnimationFrame(animFrameId);
        animFrameId = null;
      }

      if (canvas) {
        canvas.style.display = "none";
      }
      if (video) {
        video.style.opacity = "1";
      }

      // Clear frame buffer
      while (frameBuffer.length > 0) {
        const item = frameBuffer.shift();
        if (item.bitmap) item.bitmap.close();
      }
    }

    return {
      updateState(state) {
        if (state.connected && state.isStreaming && state.offsetMs > 20) {
          startDelay();
        } else {
          stopDelay();
        }
      }
    };
  }

  // Observe dynamically added videos
  const observer = new MutationObserver(() => {
    applySyncToAllVideos();
  });
  observer.observe(document.body || document.documentElement, {
    childList: true,
    subtree: true
  });
})();
