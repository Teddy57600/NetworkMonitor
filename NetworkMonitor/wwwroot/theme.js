const themeStorageKey = 'networkmonitor-theme';

function getPreferredTheme() {
    const savedTheme = localStorage.getItem(themeStorageKey);
    if (savedTheme === 'light' || savedTheme === 'dark') {
        return savedTheme;
    }

    return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

function applyTheme(theme) {
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme;
}

function updateThemeToggleButton() {
    const button = document.getElementById('themeToggleButton');
    if (!button) {
        return;
    }

    const theme = document.documentElement.dataset.theme || 'dark';
    const isLightTheme = theme === 'light';
    button.dataset.buttonLabel = isLightTheme ? 'Mode sombre' : 'Mode clair';
    button.dataset.buttonIcon = isLightTheme ? '☾' : '◐';
    button.setAttribute('aria-label', isLightTheme ? 'Activer le mode sombre' : 'Activer le mode clair');
    window.NetworkMonitorButtons?.refreshButton(button);
}

function toggleTheme() {
    const nextTheme = (document.documentElement.dataset.theme || 'dark') === 'light' ? 'dark' : 'light';
    localStorage.setItem(themeStorageKey, nextTheme);
    applyTheme(nextTheme);
    updateThemeToggleButton();
    document.dispatchEvent(new CustomEvent('networkmonitor:theme-changed', { detail: { theme: nextTheme } }));
}

function initializeTheme() {
    applyTheme(getPreferredTheme());
    updateThemeToggleButton();
    window.NetworkMonitorButtons?.enhanceAll(document);

    const button = document.getElementById('themeToggleButton');
    if (button) {
        button.addEventListener('click', toggleTheme);
    }
}

document.addEventListener('DOMContentLoaded', initializeTheme);

window.NetworkMonitorTheme = {
    getCurrentTheme() {
        return document.documentElement.dataset.theme || 'dark';
    }
};
