// Share the prescription text: native share sheet, else clipboard, else select + execCommand (plain http has neither).
// Every button with data-share-text shares (one at the top of the page, one under the text).
(function () {
  if (window.medstockShare) return;
  window.medstockShare = true;
  document.addEventListener('click', function (e) {
    var btn = e.target.closest && e.target.closest('[data-share-text]');
    if (!btn) return;
    var text = btn.dataset.shareText;
    var label = btn.textContent;
    function copied() {
      btn.textContent = btn.dataset.copied;
      setTimeout(function () { btn.textContent = label; }, 2000);
    }
    function fallback() {
      var ta = document.getElementById('share-text');
      ta.focus();
      ta.select();
      try { if (document.execCommand('copy')) copied(); } catch (err) { /* the text stays selected for a manual copy */ }
    }
    if (navigator.share) {
      navigator.share({ text: text }).catch(function (err) { if (err && err.name !== 'AbortError') fallback(); });
    } else if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(copied, fallback);
    } else {
      fallback();
    }
  });
})();
