// Copy helper used by the tokens/secret UI.
window.logrrCopy = function (text) {
  if (navigator.clipboard) { return navigator.clipboard.writeText(text); }
  return Promise.resolve();
};

// Theme: explicit choice wins, otherwise follow the OS. Persisted in localStorage.
window.logrrTheme = {
  get() {
    return localStorage.getItem('logrr-theme') || 'system';
  },
  apply(mode) {
    const root = document.documentElement;
    if (mode === 'light' || mode === 'dark') {
      root.setAttribute('data-theme', mode);
    } else {
      root.removeAttribute('data-theme');
    }
  },
  set(mode) {
    if (mode === 'system') {
      localStorage.removeItem('logrr-theme');
    } else {
      localStorage.setItem('logrr-theme', mode);
    }
    this.apply(mode);
    this.paintToggles(mode);
  },
  // Cycle: system → light → dark → system
  cycle() {
    const order = ['system', 'light', 'dark'];
    const next = order[(order.indexOf(this.get()) + 1) % order.length];
    this.set(next);
    return next;
  },
  glyph(mode) {
    return mode === 'dark' ? '☾' : mode === 'light' ? '☀' : '◐';
  },
  paintToggles(mode) {
    const g = this.glyph(mode);
    document.querySelectorAll('[data-theme-toggle]').forEach(b => { b.textContent = g; });
  },
  // Enhanced navigation re-syncs <html> against the server response, which never
  // carries data-theme, and re-renders the toggle from its static markup. Both have
  // to be restored after every enhanced page load.
  restore() {
    const mode = this.get();
    this.apply(mode);
    this.paintToggles(mode);
  },
};
