// Medication form helpers. Delegated listeners, registered once (hx-boost re-runs this script on navigation).
(function () {
  if (window.medstockEdit) return;
  window.medstockEdit = true;
  function form() { return document.querySelector('form.med-form'); }

  function syncProfile() {
    var f = form(); if (!f) return;
    var sel = f.querySelector('#profile-select');
    var box = f.querySelector('.new-profile');
    if (sel && box) box.hidden = sel.value !== 'new';
  }

  // "Every N days/weeks": the start date only matters from N = 2.
  function syncEvery() {
    var f = form(); if (!f) return;
    var every = f.querySelector('[name="Input.Every"]');
    var from = f.querySelector('.every-from');
    if (every && from) from.hidden = !(parseInt(every.value, 10) > 1);
  }

  // Intake moment rows are bound as Input.Moments[i]; keep the indexes contiguous after adding or removing a row.
  function renumber(list) {
    list.querySelectorAll('[data-moment]').forEach(function (row, i) {
      row.querySelectorAll('input').forEach(function (input) {
        input.name = input.name.replace(/Moments\[\d+\]/, 'Moments[' + i + ']');
      });
    });
    document.dispatchEvent(new Event('medstock:moments'));
  }

  function sync() { syncProfile(); syncEvery(); }

  document.addEventListener('change', function (e) {
    if (e.target.id === 'profile-select') syncProfile();
  });
  document.addEventListener('input', function (e) {
    if (e.target.name === 'Input.Every') syncEvery();
  });
  document.addEventListener('click', function (e) {
    var f = form(); if (!f || !f.contains(e.target)) return;
    if (e.target.id === 'same-every-day') {
      var days = f.querySelectorAll('.weekday');
      var v = '';
      days.forEach(function (d) { if (!v && d.value) v = d.value; });
      days.forEach(function (d) { d.value = v; });
      return;
    }
    var list = f.querySelector('[data-moments]');
    if (e.target.closest('[data-moment-add]')) {
      var rows = list.querySelectorAll('[data-moment]');
      var row = rows[rows.length - 1].cloneNode(true);
      row.querySelectorAll('input').forEach(function (input) { input.value = ''; });
      list.appendChild(row);
      renumber(list);
      row.querySelector('input').focus();
      return;
    }
    var remove = e.target.closest('[data-moment-remove]');
    if (remove) {
      var row = remove.closest('[data-moment]');
      if (list.querySelectorAll('[data-moment]').length > 1) {
        var error = row.nextElementSibling;
        if (error && error.classList.contains('error')) error.remove();
        row.remove();
      } else {
        row.querySelectorAll('input').forEach(function (input) { input.value = ''; });
      }
      renumber(list);
    }
  });
  document.addEventListener('DOMContentLoaded', sync);
  document.addEventListener('htmx:load', sync);
  sync();
})();
