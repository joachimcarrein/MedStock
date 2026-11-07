// Photo upload: resize on the device (<= 1024 px JPEG), POST with the antiforgery header, then refresh the detail.
(function () {
  if (window.medstockPhoto) return;
  window.medstockPhoto = true;

  function token() {
    try { return JSON.parse(document.body.getAttribute('hx-headers')).RequestVerificationToken; } catch (e) { return ''; }
  }

  function resize(file) {
    return new Promise(function (resolve) {
      var url = URL.createObjectURL(file);
      var img = new Image();
      img.onload = function () {
        var scale = Math.min(1, 1024 / Math.max(img.naturalWidth, img.naturalHeight));
        var c = document.createElement('canvas');
        c.width = Math.round(img.naturalWidth * scale);
        c.height = Math.round(img.naturalHeight * scale);
        c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
        URL.revokeObjectURL(url);
        c.toBlob(function (b) { resolve(b || file); }, 'image/jpeg', 0.8);
      };
      img.onerror = function () { URL.revokeObjectURL(url); resolve(file); };
      img.src = url;
    });
  }

  document.addEventListener('change', function (e) {
    var input = e.target;
    var section = input.closest && input.closest('section.photo');
    if (!section || input.type !== 'file' || !input.files.length) return;
    var error = section.querySelector('.photo-error');
    error.hidden = true;
    section.classList.add('busy');
    resize(input.files[0]).then(function (blob) {
      var fd = new FormData();
      fd.append('photo', blob, 'photo.jpg');
      return fetch(section.dataset.uploadUrl, {
        method: 'POST', body: fd, credentials: 'same-origin', headers: { 'RequestVerificationToken': token() }
      });
    }).then(function (r) {
      section.classList.remove('busy');
      if (r.status === 401 || r.status === 403) { document.getElementById('session-banner').hidden = false; return; }
      if (!r.ok) { error.hidden = false; return; }
      htmx.ajax('GET', section.dataset.detailUrl, { target: '#detail', select: '#detail', swap: 'outerHTML' });
    }).catch(function () {
      section.classList.remove('busy');
      document.getElementById('session-banner').hidden = false;
    });
  });
})();
