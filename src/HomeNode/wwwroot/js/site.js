// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// Mobile navigation drawer (narrow screens only; the toggle
// button is not rendered above the shell breakpoint). The
// rail becomes a modal drawer in the Material 3 pattern:
// opened from the top bar's house button or a swipe that
// starts at the left screen edge; closed by the scrim, a
// left swipe, Escape, or following a destination. While it
// is open, focus lives inside the drawer; on close, focus
// returns to the button that opened it. Spec:
// hidden_files/mobile-nav-oct31/spec-followed.md.
(function () {
    var toggle = document.querySelector("[data-drawer-toggle]");
    var drawer = document.getElementById("app-sidebar");
    var scrim = document.querySelector("[data-drawer-scrim]");
    if (!toggle || !drawer || !scrim) return;

    var closeTimer = null;

    function isOpen() {
        return document.body.classList.contains("drawer-open");
    }

    function focusables() {
        return drawer.querySelectorAll(
            "a[href], button:not([disabled])");
    }

    function openDrawer() {
        window.clearTimeout(closeTimer);
        scrim.hidden = false;
        // Let the scrim paint at opacity 0 before the class
        // lands, so its fade-in transition actually runs.
        void scrim.offsetWidth;
        document.body.classList.add("drawer-open");
        toggle.setAttribute("aria-expanded", "true");
        var items = focusables();
        if (items.length) items[0].focus();
    }

    function closeDrawer() {
        if (!isOpen()) return;
        document.body.classList.remove("drawer-open");
        toggle.setAttribute("aria-expanded", "false");
        var wasInside = drawer.contains(document.activeElement);
        closeTimer = window.setTimeout(function () {
            if (!isOpen()) scrim.hidden = true;
        }, 260);
        if (wasInside) toggle.focus();
    }

    toggle.addEventListener("click", function () {
        if (isOpen()) closeDrawer(); else openDrawer();
    });

    scrim.addEventListener("click", closeDrawer);

    drawer.addEventListener("click", function (event) {
        if (event.target.closest("a[href]")) closeDrawer();
    });

    document.addEventListener("keydown", function (event) {
        if (!isOpen()) return;
        if (event.key === "Escape") {
            closeDrawer();
            return;
        }
        if (event.key !== "Tab") return;
        // Keep Tab cycling inside the open drawer.
        var items = Array.prototype.slice.call(focusables());
        if (!items.length) return;
        var first = items[0];
        var last = items[items.length - 1];
        if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    });

    // Edge swipe: a touch that starts within 24px of the
    // left edge and travels 48px right opens the drawer;
    // with the drawer open, a 48px leftward travel closes
    // it. Vertical-dominant movement is left alone, so
    // scrolling the page never trips the gesture.
    var touchStart = null;

    document.addEventListener("touchstart", function (event) {
        if (event.touches.length !== 1) { touchStart = null; return; }
        var t = event.touches[0];
        touchStart = {
            x: t.clientX,
            y: t.clientY,
            fromEdge: t.clientX <= 24,
            wasOpen: isOpen()
        };
    }, { passive: true });

    document.addEventListener("touchmove", function (event) {
        if (!touchStart || event.touches.length !== 1) return;
        var t = event.touches[0];
        var dx = t.clientX - touchStart.x;
        var dy = t.clientY - touchStart.y;
        if (Math.abs(dy) > Math.abs(dx)) return;
        if (!touchStart.wasOpen && touchStart.fromEdge && dx >= 48) {
            openDrawer();
            touchStart = null;
        } else if (touchStart.wasOpen && dx <= -48) {
            closeDrawer();
            touchStart = null;
        }
    }, { passive: true });

    document.addEventListener("touchend", function () {
        touchStart = null;
    }, { passive: true });

    // If the window grows past the breakpoint with the
    // drawer open, hand navigation back to the rail.
    window.addEventListener("resize", function () {
        if (window.innerWidth > 768 && isOpen()) closeDrawer();
    });
})();
