// Barcode scanning on the device with zxing-wasm (vendored, no network): from a photo, or live from the camera.
// A button with data-scan="fill" fills the barcode field of its form and submits it; data-scan="go" opens
// data-scan-url?code=<code>. The decoder (wasm, ~1 MB) is only loaded when a scan starts.
(function () {
  if (window.medstockScan) return;
  window.medstockScan = true;

  var state = null; // { box, button, stream, timer }

  function box() { return document.getElementById('scanner'); }

  function status(text) {
    var s = box() && box().querySelector('.scanner-status');
    if (s) s.textContent = text || '';
  }

  function prepare() {
    var b = box();
    if (!window.ZXingWASM || !b) return false;
    if (!window.medstockZxingReady) {
      var wasmUrl = b.dataset.wasmUrl;
      window.ZXingWASM.prepareZXingModule({
        overrides: { locateFile: function (path, prefix) { return path.endsWith('.wasm') ? wasmUrl : prefix + path; } },
        fireImmediately: true
      });
      window.medstockZxingReady = true;
    }
    return true;
  }

  function read(input) {
    return window.ZXingWASM.readBarcodes(input, { tryHarder: true, maxNumberOfSymbols: 1 }).then(function (results) {
      var hit = (results || []).filter(function (r) { return r.isValid !== false && r.text; })[0];
      return hit ? hit.text : null;
    });
  }

  function stopLive() {
    if (!state) return;
    if (state.timer) clearTimeout(state.timer);
    state.timer = null;
    if (state.stream) state.stream.getTracks().forEach(function (t) { t.stop(); });
    state.stream = null;
    var v = box() && box().querySelector('video');
    if (v) { v.srcObject = null; v.hidden = true; }
  }

  function close() {
    stopLive();
    var b = box();
    if (b) b.hidden = true;
    state = null;
  }

  function found(code) {
    var button = state && state.button;
    close();
    if (!button) return;
    if (button.dataset.scan === 'fill') {
      var form = button.closest('form');
      form.querySelector('input[name="barcode"]').value = code;
      form.requestSubmit ? form.requestSubmit() : form.submit();
    } else {
      var url = button.dataset.scanUrl;
      window.location.href = url + (url.indexOf('?') < 0 ? '?' : '&') + 'code=' + encodeURIComponent(code);
    }
  }

  function open(button) {
    var b = box();
    if (!b || !prepare()) return;
    state = { box: b, button: button };
    b.hidden = false;
    status(b.dataset.hint);
    b.scrollIntoView({ block: 'nearest' });
  }

  function live() {
    var b = box();
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) { status(b.dataset.noCamera); return; }
    stopLive();
    var video = b.querySelector('video');
    navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' }, width: { ideal: 1280 } }, audio: false })
      .then(function (stream) {
        if (!state) { stream.getTracks().forEach(function (t) { t.stop(); }); return; }
        state.stream = stream;
        video.hidden = false;
        video.setAttribute('playsinline', '');
        video.muted = true;
        video.srcObject = stream;
        return video.play().then(function () {
          status(b.dataset.looking);
          var canvas = document.createElement('canvas');
          var ctx = canvas.getContext('2d', { willReadFrequently: true });
          function tick() {
            if (!state || !state.stream) return;
            if (video.videoWidth > 0) {
              canvas.width = video.videoWidth;
              canvas.height = video.videoHeight;
              ctx.drawImage(video, 0, 0);
              read(ctx.getImageData(0, 0, canvas.width, canvas.height)).then(function (code) {
                if (code) found(code); else if (state) state.timer = setTimeout(tick, 250);
              }, function () { if (state) state.timer = setTimeout(tick, 500); });
            } else {
              state.timer = setTimeout(tick, 250);
            }
          }
          tick();
        });
      })
      .catch(function () { status(b.dataset.noCamera); stopLive(); });
  }

  document.addEventListener('click', function (e) {
    var t = e.target.closest && e.target.closest('[data-scan], .scan-live, .scan-close');
    if (!t) return;
    if (t.matches('[data-scan]')) { e.preventDefault(); open(t); }
    else if (t.matches('.scan-live')) live();
    else close();
  });

  document.addEventListener('change', function (e) {
    if (!e.target.matches || !e.target.matches('.scan-photo') || !e.target.files || !e.target.files[0]) return;
    var b = box();
    var file = e.target.files[0];
    e.target.value = '';
    stopLive();
    status(b.dataset.looking);
    read(file).then(function (code) { if (code) found(code); else status(b.dataset.notFound); },
      function () { status(b.dataset.notFound); });
  });

  // Leaving the page (htmx navigation or back) always releases the camera.
  document.addEventListener('htmx:beforeSwap', close);
  window.addEventListener('pagehide', close);
})();
