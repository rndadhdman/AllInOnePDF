// Creator preview: keyboard and wheel paging. Page Up/Down turn the page once the view is at the bottom/top (they
// scroll a tall page first), the left and right arrows and Home/End turn pages at once, and a wheel that keeps
// going past the bottom or top of a page turns it after a short pause so reaching the edge never flips at once.

const states = new WeakMap();
const TURN_COOLDOWN_MS = 700;   // one page per wheel gesture
const EDGE_PAUSE_MS = 120;      // the wheel must dwell at the edge before a page turns

function atBottom(view) {
  return view.scrollTop + view.clientHeight >= view.scrollHeight - 2;
}

function atTop(view) {
  return view.scrollTop <= 1;
}

function turn(state, delta, where) {
  state.lastTurn = performance.now();
  state.atEdgeSince = 0;
  state.dotnet.invokeMethodAsync("TurnPage", delta, where).catch(() => { });
}

export function initPreview(root, view, dotnet) {
  if (!root || !view || states.has(root)) return;
  const state = { root, view, dotnet, atEdgeSince: 0, lastTurn: 0 };

  state.onKey = e => {
    if (e.altKey || e.ctrlKey || e.metaKey) return;
    if (e.target instanceof Element && e.target.matches("input, textarea, select, [contenteditable]")) return;
    let delta = 0;
    let where = "top";
    switch (e.key) {
      case "ArrowRight": delta = 1; break;
      case "ArrowLeft": delta = -1; break;
      case "PageDown": if (!atBottom(view)) return; delta = 1; break;
      case "PageUp": if (!atTop(view)) return; delta = -1; where = "bottom"; break;
      case "Home": delta = -1000000; break;
      case "End": delta = 1000000; break;
      default: return;
    }
    e.preventDefault();
    turn(state, delta, where);
  };

  state.onWheel = e => {
    if (e.ctrlKey) return;  // browser zoom
    const down = e.deltaY > 0;
    if (e.deltaY === 0 || !(down ? atBottom(view) : atTop(view))) { state.atEdgeSince = 0; return; }
    const now = performance.now();
    if (now - state.lastTurn < TURN_COOLDOWN_MS) return;
    if (!state.atEdgeSince) { state.atEdgeSince = now; return; }
    if (now - state.atEdgeSince < EDGE_PAUSE_MS) return;
    turn(state, down ? 1 : -1, down ? "top" : "bottom");
  };

  root.addEventListener("keydown", state.onKey);
  view.addEventListener("wheel", state.onWheel, { passive: true });
  states.set(root, state);
}

/** Scrolls the page view to its top or bottom (after the new page image has loaded). */
export function scrollView(view, where) {
  if (!view) return;
  view.scrollTop = where === "bottom" ? view.scrollHeight : 0;
}

/**
 * Keeps the current page's thumbnail visible in the strip. Only the strip scrolls: scrollIntoView would also scroll
 * every scrollable ancestor, which nudged the whole builder (and its top bar) up on End.
 */
export function revealActive(root) {
  const thumb = root?.querySelector(".cr-prev-thumb.is-on");
  const strip = thumb?.closest(".cr-prev-strip");
  if (!thumb || !strip) return;
  const s = strip.getBoundingClientRect();
  const t = thumb.getBoundingClientRect();
  if (t.top < s.top) strip.scrollTop -= s.top - t.top;
  else if (t.bottom > s.bottom) strip.scrollTop += t.bottom - s.bottom;
  if (t.left < s.left) strip.scrollLeft -= s.left - t.left;
  else if (t.right > s.right) strip.scrollLeft += t.right - s.right;
}

export function dispose(root) {
  const state = states.get(root);
  if (!state) return;
  root.removeEventListener("keydown", state.onKey);
  state.view.removeEventListener("wheel", state.onWheel);
  states.delete(root);
}
