// Resizable panels: a horizontal splitter (role="separator") above a bottom panel. Dragging runs entirely in
// the browser — pointer events + setPointerCapture, one style write per animation frame — so it stays smooth
// and never round-trips the Blazor circuit. Keyboard: ArrowUp/Down ±16 px (Shift ±64), Home/End = min/max,
// Enter or double-click = back to the default height. The height is clamped to [min, 75% of the container]
// and remembered per viewer in localStorage (best effort — storage can be blocked).
window.agentosPanelResize = {
    attach: function (handle, panel, key, defaultPx, minPx) {
        if (!handle || !panel || handle._agentosResize) { return; }
        var storeKey = 'panel-height:' + key;
        var read = function () { try { return parseInt(localStorage.getItem(storeKey), 10); } catch (e) { return NaN; } };
        var write = function (v) { try { localStorage.setItem(storeKey, String(v)); } catch (e) { /* not persisted */ } };
        var maxPx = function () {
            var host = panel.parentElement;
            return Math.max(minPx, Math.floor((host ? host.clientHeight : window.innerHeight) * 0.75));
        };
        var apply = function (px) {
            var h = Math.round(Math.min(Math.max(px, minPx), maxPx()));
            panel.style.height = h + 'px';
            panel.style.flexBasis = h + 'px';
            handle.setAttribute('aria-valuenow', String(h));
            handle.setAttribute('aria-valuemin', String(minPx));
            handle.setAttribute('aria-valuemax', String(maxPx()));
            return h;
        };
        var saved = read();
        apply(isNaN(saved) ? defaultPx : saved);

        var startY = 0, startH = 0, pending = null, frame = 0;
        var onMove = function (e) {
            pending = startH + (startY - e.clientY);   // dragging up grows the bottom panel
            if (!frame) {
                frame = requestAnimationFrame(function () { frame = 0; if (pending !== null) { apply(pending); } });
            }
        };
        var onUp = function (e) {
            handle.releasePointerCapture(e.pointerId);
            handle.removeEventListener('pointermove', onMove);
            handle.removeEventListener('pointerup', onUp);
            handle.removeEventListener('pointercancel', onUp);
            handle.classList.remove('dragging');
            document.body.classList.remove('panel-resizing');
            write(apply(pending !== null ? pending : startH));
            pending = null;
        };
        handle.addEventListener('pointerdown', function (e) {
            if (e.button !== 0) { return; }
            e.preventDefault();
            startY = e.clientY;
            startH = panel.getBoundingClientRect().height;
            pending = null;
            handle.setPointerCapture(e.pointerId);
            handle.addEventListener('pointermove', onMove);
            handle.addEventListener('pointerup', onUp);
            handle.addEventListener('pointercancel', onUp);
            handle.classList.add('dragging');
            document.body.classList.add('panel-resizing');
        });
        handle.addEventListener('dblclick', function () { write(apply(defaultPx)); });
        handle.addEventListener('keydown', function (e) {
            var h = panel.getBoundingClientRect().height, step = e.shiftKey ? 64 : 16, next = null;
            if (e.key === 'ArrowUp') { next = h + step; }
            else if (e.key === 'ArrowDown') { next = h - step; }
            else if (e.key === 'Home') { next = minPx; }
            else if (e.key === 'End') { next = maxPx(); }
            else if (e.key === 'Enter') { next = defaultPx; }
            if (next !== null) { e.preventDefault(); write(apply(next)); }
        });
        // Keep the panel inside its clamp when the window is resized.
        var ro = new ResizeObserver(function () { apply(panel.getBoundingClientRect().height); });
        if (panel.parentElement) { ro.observe(panel.parentElement); }
        handle._agentosResize = { ro: ro };
    },
    detach: function (handle) {
        if (handle && handle._agentosResize) { handle._agentosResize.ro.disconnect(); handle._agentosResize = null; }
    },
};
