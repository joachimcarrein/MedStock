// Ingress sessions expire after inactivity: show a banner that reloads the whole HA frontend.
(function () {
  function show() {
    var b = document.getElementById('session-banner');
    if (b) b.hidden = false;
  }
  document.addEventListener('htmx:responseError', function (e) {
    var s = e.detail && e.detail.xhr ? e.detail.xhr.status : 0;
    if (s === 401 || s === 403) show();
  });
  document.addEventListener('htmx:sendError', show);
  // 409 (someone else changed it) and 400/422 (validation) carry a re-rendered form: let htmx swap it.
  document.addEventListener('htmx:beforeSwap', function (e) {
    var s = e.detail.xhr.status;
    if (s === 409 || s === 400 || s === 422) { e.detail.shouldSwap = true; e.detail.isError = false; }
  });
})();
