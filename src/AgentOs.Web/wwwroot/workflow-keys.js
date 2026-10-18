// Global ⌘K / Ctrl+K for the Workflow builder. The OrchestrationStudio component registers a .NET ref
// on first render and unregisters on dispose; there is at most one Workflow window (the window manager
// focuses the existing one), so a single document-level listener is sufficient. Capture phase + a guard
// against firing while the palette input itself is focused keeps it from hijacking normal typing.
window.agentosWorkflowKeys = {
    _handler: null,
    register: function (dotnetRef) {
        this.unregister();
        this._handler = function (e) {
            var mod = e.ctrlKey || e.metaKey;
            if (!mod) { return; }
            // Only while the Workflow window is the focused one: otherwise Ctrl+K belongs to the shell's search
            // (shell-keys.js) and Ctrl+Z/Y to whatever field has focus elsewhere.
            if (!document.querySelector('.appwin.focused .syn-toolbar')) { return; }
            var k = e.key.toLowerCase();
            if (k === 'k') {
                e.preventDefault();
                dotnetRef.invokeMethodAsync('OnQuickAddKey');
            } else if (k === 'z' && !e.shiftKey) {
                e.preventDefault();
                dotnetRef.invokeMethodAsync('OnUndoKey');
            } else if ((k === 'z' && e.shiftKey) || k === 'y') {
                e.preventDefault();
                dotnetRef.invokeMethodAsync('OnRedoKey');
            }
        };
        document.addEventListener('keydown', this._handler, true);
    },
    unregister: function () {
        if (this._handler) {
            document.removeEventListener('keydown', this._handler, true);
            this._handler = null;
        }
    }
};
