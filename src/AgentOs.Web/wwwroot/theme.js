// AgentOS.NET theming: persisted in localStorage, applied to <html data-theme / data-wallpaper>.
// Three controls:
//   theme      : 'light' | 'dark'
//   wallpaper  : 'enterprise-light' | 'enterprise-dark' | 'aurora' | 'midnight' | 'sunset'
//   glass      : 0..100 (percent) → --glass-blur in pixels (0..32px)
//   agentMotion: 'on' | 'off' → <html data-agent-motion> (AgentMascot animations; reduce-motion always wins)
window.agenticTheme = {
    THEME_KEY: 'theme',
    WALL_KEY: 'wallpaper',
    GLASS_KEY: 'glass',
    AGENT_MOTION_KEY: 'agent-motion',

    getTheme: function () { return localStorage.getItem(this.THEME_KEY) || 'light'; },
    getWallpaper: function () { return localStorage.getItem(this.WALL_KEY) || 'enterprise-light'; },
    getGlass: function () { var g = parseInt(localStorage.getItem(this.GLASS_KEY), 10); return isNaN(g) ? 0 : g; },
    getAgentMotion: function () { return window.agenticTheme.readPref(window.agenticTheme.AGENT_MOTION_KEY) === 'off' ? 'off' : 'on'; },

    // Storage can be blocked (private mode, enterprise policy) — a preference must never break the shell.
    readPref: function (key) { try { return localStorage.getItem(key); } catch (e) { return null; } },
    writePref: function (key, value) { try { localStorage.setItem(key, value); } catch (e) { /* not persisted */ } },

    applyTheme: function (t) {
        document.documentElement.dataset.theme = t;
        localStorage.setItem(this.THEME_KEY, t);
        return t;
    },
    applyWallpaper: function (w) {
        document.documentElement.dataset.wallpaper = w;
        localStorage.setItem(this.WALL_KEY, w);
        return w;
    },
    applyGlass: function (percent) {
        var p = Math.max(0, Math.min(100, parseInt(percent, 10) || 0));
        var blurPx = Math.round((p / 100) * 32);
        document.documentElement.style.setProperty('--glass-blur', blurPx + 'px');
        document.documentElement.style.setProperty('--glass-saturate', (100 + p) + '%');
        // 0% = NO backdrop filter at all. Even blur(0px) makes the browser build a backdrop layer and re-run
        // it whenever anything inside the window repaints — measured as a 60 → 20 fps drop with a few
        // windows open. Only a real glass setting pays that cost.
        document.documentElement.style.setProperty('--glass-filter',
            p > 0 ? 'blur(' + blurPx + 'px) saturate(' + (100 + p) + '%)' : 'none');
        localStorage.setItem(this.GLASS_KEY, p);
        return p;
    },
    applyAgentMotion: function (v) {
        var T = window.agenticTheme;   // not `this` — Blazor interop drops the receiver.
        var value = v === 'off' ? 'off' : 'on';
        document.documentElement.dataset.agentMotion = value;
        T.writePref(T.AGENT_MOTION_KEY, value);
        return value;
    },
    toggleTheme: function () {
        // Use the explicit reference, not `this` — Blazor's JS interop invokes this
        // by dotted path without preserving the `agenticTheme` receiver.
        var T = window.agenticTheme;
        var next = T.getTheme() === 'light' ? 'dark' : 'light';
        return T.applyTheme(next);
    },

    // One-click appearance toggle for the TopBar. Flips the theme and, when the
    // wallpaper is one of the paired enterprise variants, flips it to match so the
    // desktop stays coherent. Custom wallpapers (aurora/midnight/sunset) are left
    // as the user chose them. Returns the new theme.
    // Apply an EXPLICIT theme and, when the wallpaper is a paired enterprise variant, flip it to
    // match so the desktop stays coherent. Custom wallpapers (aurora/midnight/sunset) are left as
    // the user chose them. Shared by the TopBar toggle AND the Settings dropdown so the two controls
    // behave identically (the source of the previous TopBar↔Settings desync).
    applyThemePaired: function (t) {
        var T = window.agenticTheme;
        T.applyTheme(t);
        var w = T.getWallpaper();
        if (w === 'enterprise-light' || w === 'enterprise-dark') {
            T.applyWallpaper(t === 'dark' ? 'enterprise-dark' : 'enterprise-light');
        }
        return t;
    },
    toggleAppearance: function () {
        var T = window.agenticTheme;   // not `this` — see toggleTheme note.
        return T.applyThemePaired(T.getTheme() === 'light' ? 'dark' : 'light');
    },

    // Back-compat with old call sites that used agenticTheme.apply / agenticTheme.toggle / agenticTheme.get
    get: function () { return this.getTheme(); },
    apply: function (t) { return this.applyTheme(t); },
    toggle: function () { return this.toggleTheme(); },

    restoreAll: function () {
        this.applyTheme(this.getTheme());
        this.applyWallpaper(this.getWallpaper());
        this.applyGlass(this.getGlass());
        this.applyAgentMotion(this.getAgentMotion());
    },
};

// Apply persisted theming before Blazor mounts to avoid flash.
window.agenticTheme.restoreAll();

// Auth — Phase 8.3. Single place to clear every persisted auth key on logout/lock so a stale
// JWT can't linger after sign-out.
window.agenticAuth = {
    AUTH_KEYS: ['agentic-jwt', 'agentic-user', 'agentic-jwt-exp', 'agentic-signed-in'],
    signOut: function () {
        this.AUTH_KEYS.forEach(function (k) { localStorage.removeItem(k); });
    },
};
