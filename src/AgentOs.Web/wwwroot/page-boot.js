// Page wiring that used to live in inline attributes of App.razor. The Content-Security-Policy is script-src 'self'
// (GAP-12), which forbids inline event handlers, so they are attached from this file instead:
//   • <link data-async-style> — a stylesheet fetched with media="print" (non-blocking) switches to all media on load;
//   • [data-reconnect="retry" | "reload"] — the reconnect modal's buttons.
(function () {
    document.querySelectorAll('link[data-async-style]').forEach(function (link) {
        var apply = function () { link.media = 'all'; };
        if (link.sheet) { apply(); } else { link.addEventListener('load', apply, { once: true }); }
    });

    document.addEventListener('click', function (e) {
        var target = e.target instanceof Element ? e.target.closest('[data-reconnect]') : null;
        if (!target) { return; }
        if (target.getAttribute('data-reconnect') === 'retry' && window.Blazor) {
            window.Blazor.reconnect();
        } else {
            location.reload();
        }
    });
})();
