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
  },
  // Cycle: system → light → dark → system
  cycle() {
    const order = ['system', 'light', 'dark'];
    const next = order[(order.indexOf(this.get()) + 1) % order.length];
    this.set(next);
    return next;
  },
};
