// Recount and initial stock: shows what will be recorded before saving. A count is the stock now; MedStock keeps the stock
// at the end of the day, so the doses that still follow today are subtracted. The server does the same calculation on save.
// The detail page passes today's remaining doses (data-pending); the new-medication form works them out from its own fields.
(function () {
  if (window.medstockRecount) return;
  window.medstockRecount = true;

  function num(v) { var n = parseFloat(String(v == null ? '' : v).replace(',', '.')); return isNaN(n) ? 0 : n; }
  function field(f, names) {
    for (var i = 0; i < names.length; i++) { var el = f.querySelector('[name="' + names[i] + '"]'); if (el) return el; }
    return null;
  }
  function minutes(hhmm) { if (!hhmm) return null; var p = hhmm.split(':'); return +p[0] * 60 + +p[1]; }
  function dayNumber(y, m, d) { return Math.floor(Date.UTC(y, m, d) / 864e5); }
  function monday(dn) { return dn - ((dn + 3) % 7); } // day 0 (1 Jan 1970) was a Thursday
  function fmt(n) { return String(Math.round(n * 100) / 100); }

  // Today's remaining doses from the medication form (new medication): [{t: "20:00" | null, a: amount}].
  function pendingFromForm(f) {
    var mode = (f.querySelector('input[name="Input.UsageMode"]:checked') || {}).value;
    if (mode !== 'PerDay' && mode !== 'Weekly') return [];
    var now = new Date();
    var nowMin = now.getHours() * 60 + now.getMinutes();
    var untimed = minutes(f.dataset.untimedFrom);
    var today = dayNumber(now.getFullYear(), now.getMonth(), now.getDate());
    var weekday = (now.getDay() + 6) % 7; // Monday = 0

    var every = Math.max(1, Math.floor(num((field(f, ['Input.Every']) || {}).value)) || 1);
    var fromValue = (field(f, ['Input.EveryFrom']) || {}).value;
    if (every > 1 && fromValue) {
      var p = fromValue.split('-');
      var from = dayNumber(+p[0], +p[1] - 1, +p[2]);
      var diff = mode === 'Weekly' ? (monday(today) - monday(from)) / 7 : today - from;
      if (((diff % every) + every) % every !== 0) return [];
    }
    function pending(t) { var m = minutes(t); return nowMin < (m == null ? untimed : m); }

    if (mode === 'Weekly') {
      var amount = num((field(f, ['Input.Weekly[' + weekday + ']']) || {}).value);
      var time = (field(f, ['Input.WeeklyTime']) || {}).value || null;
      return amount > 0 && pending(time) ? [{ t: time, a: amount }] : [];
    }
    var list = [];
    f.querySelectorAll('[data-moment]').forEach(function (row) {
      var t = (row.querySelector('input[type="time"]') || {}).value || null;
      var a = num((row.querySelector('input[type="number"]') || {}).value);
      if (a > 0 && pending(t)) list.push({ t: t, a: a });
    });
    return list;
  }

  function render(f) {
    var note = f.querySelector('[data-recount-note]');
    if (!note) return;
    var open = field(f, ['openUnits', 'Input.OpenUnits']);
    var sealed = field(f, ['sealedPackages', 'Input.SealedPackages']);
    if (!open || !sealed || (open.value === '' && sealed.value === '')) { note.hidden = true; return; }

    var type = f.dataset.type || (field(f, ['Input.Type']) || {}).value;
    var names = JSON.parse(f.dataset.names || '{}')[type] || ['', '', '', ''];
    var upp = f.dataset.upp ? num(f.dataset.upp) : num((field(f, ['Input.UnitsPerPackage']) || {}).value);
    var pending = f.dataset.pending ? JSON.parse(f.dataset.pending) : pendingFromForm(f);
    var deduct = pending.reduce(function (s, x) { return s + num(x.a); }, 0);

    var total = Math.max(0, num(open.value) + Math.floor(num(sealed.value)) * upp - deduct);
    var packages = upp > 0 ? Math.floor(total / upp) : 0;
    var rest = Math.floor(total - packages * upp);
    var units = rest + ' ' + (rest === 1 ? names[0] : names[1]);
    var qty = packages === 0 ? units
      : packages + ' ' + (packages === 1 ? names[2] : names[3]) + (rest === 0 ? '' : ' + ' + units);

    var text = f.dataset.result.replace('{0}', qty);
    if (deduct > 0) {
      var times = pending.map(function (x) { return x.t || f.dataset.noTime; }).join(', ');
      text += ' ' + f.dataset.deducted
        .replace('{0}', fmt(deduct) + ' ' + (deduct === 1 ? names[0] : names[1]))
        .replace('{1}', times);
    }
    if (pending.some(function (x) { return !x.t; })) text += ' ' + f.dataset.untimedHint;
    note.textContent = text;
    note.hidden = false;
  }

  function renderAll() { document.querySelectorAll('form[data-recount]').forEach(render); }
  function onEvent(e) {
    var f = e.target.closest && e.target.closest('form[data-recount]');
    if (f) render(f);
  }
  document.addEventListener('input', onEvent);
  document.addEventListener('change', onEvent);
  document.addEventListener('medstock:moments', renderAll);
  document.addEventListener('DOMContentLoaded', renderAll);
  document.addEventListener('htmx:load', renderAll);
  renderAll();
})();
