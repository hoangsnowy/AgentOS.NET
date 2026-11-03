// Global Ctrl/⌘+K → the Activities search (apps, settings, actions). AppShellLayout registers a .NET ref on first
// render. The Workflow builder keeps Ctrl+K for "Add a node" while its window is focused: workflow-keys.js
// listens in the capture phase and prevents default, so this bubble-phase listener skips handled events.
window.agentosShellKeys = {
    _handler: null,
    register: function (dotnetRef) {
        this.unregister();
        this._handler = function (e) {
            if (e.defaultPrevented || e.shiftKey || e.altKey || !(e.ctrlKey || e.metaKey)) { return; }
            if (e.key.toLowerCase() !== 'k') { return; }
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnCommandKey');
        };
        document.addEventListener('keydown', this._handler);
    },
    unregister: function () {
        if (this._handler) {
            document.removeEventListener('keydown', this._handler);
            this._handler = null;
        }
    }
};

// GAP-19: sign-out is a POST with an antiforgery token. App.razor renders the form (#agentos-signout) with the token;
// Sign out / Lock submit it. Without the form (it should always be there) the GET page asks to confirm instead.
window.agentosAccount = {
    signOut: function () {
        var form = document.getElementById('agentos-signout');
        if (form) { form.submit(); } else { location.href = '/account/logout'; }
    }
};
