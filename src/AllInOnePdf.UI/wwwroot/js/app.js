// Small JS helpers used by AllInOnePdf.UI via IJSRuntime. Keep this file tiny; prefer Blazor.
window.aio = window.aio || {};

window.aio._saveBlob = function (fileName, blob) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  a.rel = "noopener";
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
};

// Triggers a browser download from a DotNetStreamReference (used by the web host instead of a native save dialog).
// The bytes stream over the circuit in chunks; no base64 string is built on either side.
window.aio.downloadStream = async function (fileName, mediaType, streamRef) {
  const buffer = await streamRef.arrayBuffer();
  window.aio._saveBlob(fileName, new Blob([buffer], { type: mediaType || "application/octet-stream" }));
};

window.aio.prefersReducedMotion = function () {
  return !!(window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches);
};

window.aio.openUrl = function (url) {
  window.open(url, "_blank", "noopener,noreferrer");
};

window.aio.copyText = async function (text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
};

// Scrolls an element with the given id into view (settings sections, reader fallback). Instant with reduced motion.
window.aio.scrollToId = function (id) {
  const el = document.getElementById(id);
  if (!el) return false;
  el.scrollIntoView({ behavior: window.aio.prefersReducedMotion() ? "auto" : "smooth", block: "start" });
  return true;
};

// Skip link ("Skip to page content"): moves focus to the target (the shell's <main>, tabindex=-1) without a
// server round trip, so the next Tab lands on the page's first control.
document.addEventListener("click", function (e) {
  const skip = e.target instanceof Element ? e.target.closest("[data-aio-skip-to]") : null;
  if (!skip) return;
  const target = document.getElementById(skip.getAttribute("data-aio-skip-to"));
  if (!target) return;
  e.preventDefault();
  target.focus({ preventScroll: true });
  target.scrollTop = 0;
});

// Settings section list: reports which section is at the top of the scroll area as the user scrolls, so the list
// highlights it. dotnetRef.OnSectionVisible(id) is called when the current section changes.
window.aio._sectionSpies = window.aio._sectionSpies || new Map();
window.aio.watchSections = function (dotnetRef, key, ids) {
  window.aio.unwatchSections(key);
  const sections = ids.map((id) => document.getElementById(id)).filter(Boolean);
  if (sections.length === 0) return;
  let current = null;
  let frame = 0;
  let holdUntil = 0;
  const pick = () => {
    frame = 0;
    // After a click in the list the smooth scroll passes other sections; keep the clicked one while it runs.
    if (performance.now() < holdUntil) return;
    // Of the sections whose top has passed a line a third down the viewport, the one that passed it last (top nearest
    // the line; on a tie the first in document order, so cards side by side pick the left column); at the very
    // bottom, the last one.
    const line = window.innerHeight / 3;
    let active = sections[0];
    let activeTop = -Infinity;
    for (const s of sections) {
      const top = s.getBoundingClientRect().top;
      if (top <= line && top > activeTop + 1) {
        active = s;
        activeTop = top;
      }
    }
    const scroller = document.getElementById("aio-main");
    if (scroller && scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 4) {
      active = sections[sections.length - 1];
    }
    if (active.id !== current) {
      current = active.id;
      dotnetRef.invokeMethodAsync("OnSectionVisible", current).catch(() => { });
    }
  };
  // A timer rather than requestAnimationFrame: frames do not run while the window is in the background.
  const onScroll = () => { if (!frame) frame = setTimeout(pick, 80); };
  document.addEventListener("scroll", onScroll, { capture: true, passive: true });
  const stop = () => {
    document.removeEventListener("scroll", onScroll, { capture: true });
    if (frame) clearTimeout(frame);
  };
  stop.hold = (id) => { current = id; holdUntil = performance.now() + 1000; };
  window.aio._sectionSpies.set(key, stop);
};
// Scrolls to a section picked in the list and keeps it highlighted while the scroll runs.
window.aio.jumpToSection = function (key, id) {
  const spy = window.aio._sectionSpies.get(key);
  if (spy && spy.hold) spy.hold(id);
  return window.aio.scrollToId(id);
};
window.aio.unwatchSections = function (key) {
  const stop = window.aio._sectionSpies.get(key);
  if (stop) {
    stop();
    window.aio._sectionSpies.delete(key);
  }
};

// Escape closes an open menu whose button opts in with data-aio-escape-closes: FluentMenu renders its items in the
// menu provider, outside the button's markup, so a Blazor key handler next to the button never sees the key. The
// button's own click handler closes the menu; focus returns to the button.
document.addEventListener("keydown", function (e) {
  if (e.key !== "Escape") return;
  const button = document.querySelector('[data-aio-escape-closes][aria-expanded="true"]');
  if (!button) return;
  e.preventDefault();
  button.click();
  button.focus();
}, true);

// Desktop title bar (AppHeader when IHostServices.SupportsWindowControls). Pressing on an empty part of the header
// and moving the pointer hands the drag to the OS (dotnetRef.BeginWindowDrag("Move")), which moves the window with
// snapping and restores a maximized window under the pointer. The top-edge handles ([data-aio-resize]) do the same
// for resizing. Double-click toggles maximize. If the host cannot hand the drag over, the window is moved from here
// with screenX/screenY deltas, coalesced to one MoveWindowBy call per animation frame.
window.aio._titleBars = window.aio._titleBars || new Map();
window.aio.titleBar = {
  // Controls keep their own pointer behaviour; everything else in the header is the drag region.
  _interactive: "button, a, input, select, textarea, [role='menu'], [role='menuitem'], [role='menuitemradio'], fluent-button, fluent-menu, fluent-menu-item, [data-aio-no-drag]",

  attach(element, dotnetRef, key) {
    window.aio.titleBar.detach(key);
    if (!element) return;
    const threshold = 3; // px of movement before a press becomes a drag, so clicks and double-clicks stay clicks
    let press = null;    // { id, x, y, op, fallback, lastX, lastY, dx, dy, frame }

    const operationFor = (target) => {
      if (!(target instanceof Element)) return null;
      const edge = target.closest("[data-aio-resize]");
      if (edge) return edge.getAttribute("data-aio-resize");
      return target.closest(window.aio.titleBar._interactive) ? null : "Move";
    };

    const flush = () => {
      if (!press) return;
      press.frame = 0;
      const dx = Math.round(press.dx), dy = Math.round(press.dy);
      if (dx === 0 && dy === 0) return;
      press.dx -= dx;
      press.dy -= dy;
      dotnetRef.invokeMethodAsync("MoveWindowBy", dx, dy).catch(() => { });
    };

    const end = () => {
      if (!press) return;
      if (press.frame) cancelAnimationFrame(press.frame);
      flush();
      try { element.releasePointerCapture(press.id); } catch { }
      press = null;
    };

    const onDown = (e) => {
      if (e.button !== 0 || !e.isPrimary) return;
      const op = operationFor(e.target);
      if (!op) return;
      press = { id: e.pointerId, x: e.screenX, y: e.screenY, op, started: false, fallback: false, lastX: e.screenX, lastY: e.screenY, dx: 0, dy: 0, frame: 0 };
      try { element.setPointerCapture(e.pointerId); } catch { }
    };

    const onMove = (e) => {
      if (!press || e.pointerId !== press.id) return;
      if (press.fallback) {
        const ratio = window.devicePixelRatio || 1;
        press.dx += (e.screenX - press.lastX) * ratio;
        press.dy += (e.screenY - press.lastY) * ratio;
        press.lastX = e.screenX;
        press.lastY = e.screenY;
        if (!press.frame) press.frame = requestAnimationFrame(flush);
        return;
      }
      if (press.started) return;
      if (Math.abs(e.screenX - press.x) < threshold && Math.abs(e.screenY - press.y) < threshold) return;
      press.started = true;
      const current = press;
      // The OS takes the mouse from here on; the webview does not see the release.
      try { element.releasePointerCapture(current.id); } catch { }
      dotnetRef.invokeMethodAsync("BeginWindowDrag", current.op).then((handed) => {
        if (handed) {
          if (press === current) press = null;
        } else if (press === current && current.op === "Move") {
          current.fallback = true;
          current.lastX = e.screenX;
          current.lastY = e.screenY;
          try { element.setPointerCapture(current.id); } catch { }
        }
      }).catch(() => { if (press === current) press = null; });
    };

    const onDblClick = (e) => {
      if (operationFor(e.target) !== "Move") return;
      e.preventDefault();
      dotnetRef.invokeMethodAsync("ToggleMaximize").catch(() => { });
    };

    element.addEventListener("pointerdown", onDown);
    element.addEventListener("pointermove", onMove);
    element.addEventListener("pointerup", end);
    element.addEventListener("pointercancel", end);
    element.addEventListener("dblclick", onDblClick);
    window.aio._titleBars.set(key, () => {
      end();
      element.removeEventListener("pointerdown", onDown);
      element.removeEventListener("pointermove", onMove);
      element.removeEventListener("pointerup", end);
      element.removeEventListener("pointercancel", end);
      element.removeEventListener("dblclick", onDblClick);
    });
  },

  detach(key) {
    const stop = window.aio._titleBars.get(key);
    if (stop) {
      stop();
      window.aio._titleBars.delete(key);
    }
  },
};

// Reads the current scroll fraction (0..1) of a scrollable element, for reading progress.
window.aio.getScrollFraction = function (element) {
  if (!element) return 0;
  const max = element.scrollHeight - element.clientHeight;
  return max <= 0 ? 1 : element.scrollTop / max;
};

// Returns the OS colour scheme so the app can follow "System" theme mode.
window.aio.prefersDark = function () {
  return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
};

// Calls dotnetRef.OnSystemDarkChanged(bool) whenever the OS colour scheme changes. Returns the current value.
// `key` identifies the watcher so aio.unwatchColorScheme(key) can stop it.
window.aio._schemeWatchers = window.aio._schemeWatchers || new Map();
window.aio.watchColorScheme = function (dotnetRef, key) {
  if (!window.matchMedia) return false;
  const mq = window.matchMedia("(prefers-color-scheme: dark)");
  window.aio.unwatchColorScheme(key);
  const handler = () => dotnetRef.invokeMethodAsync("OnSystemDarkChanged", mq.matches).catch(() => { });
  mq.addEventListener("change", handler);
  window.aio._schemeWatchers.set(key, () => mq.removeEventListener("change", handler));
  return mq.matches;
};
window.aio.unwatchColorScheme = function (key) {
  const stop = window.aio._schemeWatchers.get(key);
  if (stop) {
    stop();
    window.aio._schemeWatchers.delete(key);
  }
};
