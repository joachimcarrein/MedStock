// Overview search: hides the cards whose name and strength do not contain the typed text. The server already limits the
// cards to the chosen profile, so the search works within that filter. Case and accents are ignored ("e" finds "é").
(function () {
  if (window.medstockSearch) return;
  window.medstockSearch = true;

  function fold(s) {
    return (s || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().trim();
  }

  function apply(input) {
    var wrap = input.closest('.clearable');
    var clear = wrap && wrap.querySelector('[data-search-clear]');
    if (clear) clear.hidden = input.value === '';
    var terms = fold(input.value).split(/\s+/).filter(Boolean);
    var shown = 0;
    document.querySelectorAll('[data-search-text]').forEach(function (li) {
      var text = fold(li.dataset.searchText);
      var match = terms.every(function (t) { return text.indexOf(t) >= 0; });
      li.hidden = !match;
      if (match) shown++;
    });
    var none = document.querySelector('[data-search-none]');
    if (none) none.hidden = shown > 0;
  }

  document.addEventListener('input', function (e) {
    if (e.target.matches && e.target.matches('[data-search]')) apply(e.target);
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && e.target.matches && e.target.matches('[data-search]')) {
      e.target.value = '';
      apply(e.target);
    }
  });
  document.addEventListener('click', function (e) {
    var btn = e.target.closest && e.target.closest('[data-search-clear]');
    if (!btn) return;
    var input = btn.closest('.clearable').querySelector('[data-search]');
    input.value = '';
    apply(input);
    input.focus();
  });
})();
