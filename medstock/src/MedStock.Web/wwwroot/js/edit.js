// Medication form helpers. Delegated listeners, registered once (hx-boost re-runs this script on navigation).
(function () {
  if (window.medstockEdit) return;
  window.medstockEdit = true;
  function num(el) { var v = parseFloat(String(el.value || '').replace(',', '.')); return isNaN(v) ? 0 : v; }
  function round(v) { return Math.round(v * 1000) / 1000; }
  function form() { return document.querySelector('form.med-form'); }

  function syncProfile() {
    var f = form(); if (!f) return;
    var sel = f.querySelector('#profile-select');
    var box = f.querySelector('.new-profile');
    if (sel && box) box.hidden = sel.value !== 'new';
  }

  document.addEventListener('input', function (e) {
    var f = form(); if (!f || !f.contains(e.target)) return;
    var perDay = f.querySelector('#perday');
    if (e.target.id === 'h-times' || e.target.id === 'h-per') {
      perDay.value = round(num(f.querySelector('#h-times')) * num(f.querySelector('#h-per')));
    } else if (e.target.classList.contains('h-part')) {
      var sum = 0;
      f.querySelectorAll('.h-part').forEach(function (p) { sum += num(p); });
      perDay.value = round(sum);
    }
  });
  document.addEventListener('change', function (e) {
    if (e.target.id === 'profile-select') syncProfile();
  });
  document.addEventListener('click', function (e) {
    if (e.target.id !== 'same-every-day') return;
    var days = form().querySelectorAll('.weekday');
    var v = '';
    days.forEach(function (d) { if (!v && d.value) v = d.value; });
    days.forEach(function (d) { d.value = v; });
  });
  document.addEventListener('DOMContentLoaded', syncProfile);
  document.addEventListener('htmx:load', syncProfile);
  syncProfile();
})();
