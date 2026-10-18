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
