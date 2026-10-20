// Opens Plaid Link and reports the result back to the Blazor component. successMethod defaults to
// PlaidLinkSuccess (new connection); update mode (e.g. approving investments) passes its own.
window.openPlaidLink = function (linkToken, dotNetRef, successMethod) {
    successMethod = successMethod || "PlaidLinkSuccess";
    var handler = null;
    var report = function (method) {
        var args = Array.prototype.slice.call(arguments, 1);
        dotNetRef.invokeMethodAsync.apply(dotNetRef, [method].concat(args)).catch(function (e) { console.error(e); });
    };
    try {
        if (typeof Plaid === 'undefined' || typeof Plaid.create !== 'function') {
            report('PlaidLinkError', 'The Plaid Link script did not load (check your network or ad blocker).');
            return;
        }
        handler = Plaid.create({
            token: linkToken,
            onSuccess: function (publicToken, metadata) {
                var name = (metadata && metadata.institution && metadata.institution.name) || '';
                report(successMethod, publicToken || '', name);
                if (handler) handler.destroy();
            },
            onExit: function (error) {
                report('PlaidLinkError', error ? (error.display_message || error.error_message || error.error_code || 'error') : 'cancelled');
                if (handler) handler.destroy();
            }
        });
        handler.open();
    } catch (e) {
        report('PlaidLinkError', String(e));
    }
};

// Hover readout for line charts rendered server-side. Each chart is an <svg class="linechart">
// with data-points='[{"x":px,"y":px,"label":"…","value":"…"}]'. Uses event delegation, so
// it keeps working when Blazor re-renders the chart.
(function () {
    function setup(svg) {
        if (svg._points) return svg._points;
        try { svg._points = JSON.parse(svg.getAttribute('data-points') || '[]'); } catch (e) { svg._points = []; }
        return svg._points;
    }
    function hide(svg) {
        var g = svg.querySelector('.hover');
        if (g) g.style.display = 'none';
        var tip = svg.parentElement.querySelector('.tip');
        if (tip) tip.style.display = 'none';
    }
    function show(svg, clientX) {
        var pts = setup(svg);
        if (!pts.length) return;
        var box = svg.getBoundingClientRect();
        var vb = svg.viewBox.baseVal;
        var x = (clientX - box.left) * vb.width / box.width;
        var best = pts[0];
        for (var i = 1; i < pts.length; i++) if (Math.abs(pts[i].x - x) < Math.abs(best.x - x)) best = pts[i];
        var g = svg.querySelector('.hover');
        g.style.display = '';
        g.querySelector('line').setAttribute('x1', best.x);
        g.querySelector('line').setAttribute('x2', best.x);
        g.querySelector('circle').setAttribute('cx', best.x);
        g.querySelector('circle').setAttribute('cy', best.y);
        var tip = svg.parentElement.querySelector('.tip');
        tip.querySelector('b').textContent = best.value;
        tip.querySelector('span').textContent = best.label;
        tip.style.display = 'block';
        var left = best.x * box.width / vb.width;
        var tw = tip.offsetWidth;
        tip.style.left = Math.max(0, Math.min(box.width - tw, left - tw / 2)) + 'px';
        tip.style.top = Math.max(0, best.y * box.height / vb.height - tip.offsetHeight - 14) + 'px';
    }
    document.addEventListener('pointermove', function (e) {
        var svg = e.target.closest && e.target.closest('svg.linechart');
        document.querySelectorAll('svg.linechart').forEach(function (s) { if (s !== svg) hide(s); });
        if (svg) { svg._points = null; show(svg, e.clientX); }
    });
    document.addEventListener('pointerleave', function () {
        document.querySelectorAll('svg.linechart').forEach(hide);
    });
})();

// Hover readout for any element with data-tip="Title|line 1|line 2" (bars, segments, cells).
// Built with textContent only; one floating box shared by the whole page.
(function () {
    var box = null;
    function ensure() {
        if (box) return box;
        box = document.createElement('div');
        box.className = 'float-tip';
        document.body.appendChild(box);
        return box;
    }
    function render(text) {
        var b = ensure();
        b.textContent = '';
        text.split('|').forEach(function (line, i) {
            var el = document.createElement(i === 0 ? 'b' : 'div');
            el.textContent = line;
            b.appendChild(el);
        });
    }
    document.addEventListener('pointermove', function (e) {
        var t = e.target.closest && e.target.closest('[data-tip]');
        if (!t) { if (box) box.style.display = 'none'; return; }
        render(t.getAttribute('data-tip'));
        var b = ensure();
        b.style.display = 'block';
        var x = e.clientX + 14, y = e.clientY + 14;
        if (x + b.offsetWidth > window.innerWidth - 8) x = e.clientX - b.offsetWidth - 14;
        if (y + b.offsetHeight > window.innerHeight - 8) y = e.clientY - b.offsetHeight - 14;
        b.style.left = x + 'px';
        b.style.top = y + 'px';
    });
    document.addEventListener('scroll', function () { if (box) box.style.display = 'none'; }, true);
})();
