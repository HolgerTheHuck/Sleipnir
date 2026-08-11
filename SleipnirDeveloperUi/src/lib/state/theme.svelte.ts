type Theme = 'dark' | 'light';

const STORAGE_KEY = 'sleipnir-theme';

function getInitialTheme(): Theme {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === 'light' || stored === 'dark') return stored;
  } catch {
    /* ignore */
  }
  return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

function applyTheme(theme: Theme) {
  document.documentElement.setAttribute('data-theme', theme);
  try {
    localStorage.setItem(STORAGE_KEY, theme);
  } catch {
    /* ignore */
  }
}

class ThemeState {
  theme = $state<Theme>(getInitialTheme());

  constructor() {
    applyTheme(this.theme);
  }

  toggle() {
    this.theme = this.theme === 'dark' ? 'light' : 'dark';
    applyTheme(this.theme);
  }

  /** Setzt ein explizites Theme (Workspace-Import). Ignoriert ungültige Werte. */
  set(theme: Theme) {
    if (theme !== 'dark' && theme !== 'light') return;
    this.theme = theme;
    applyTheme(this.theme);
  }
}

export const themeState = new ThemeState();
