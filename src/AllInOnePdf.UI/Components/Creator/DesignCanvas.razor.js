// Creator design canvas: hover highlighting, zoom, and HTML5 drag and drop with an insertion line.
// The same module also wires the structure tree and the Insert tiles, so a drag can start in one place and end in
// another (tiles → canvas or tree). Drops are reported to .NET as (kind, id, targetId, index); "index" is the
// position in the target list as it looks before the dragged element is taken out.

let drag = null;          // { kind: "module" | "row" | "section" | "new", id }
let line = null;          // the insertion line (one, fixed-positioned in <body>)
const states = new WeakMap();

function ensureLine() {
  if (line && document.body.contains(line)) return line;
  line = document.createElement("div");
  line.className = "cr-dropline";
  line.setAttribute("aria-hidden", "true");
  Object.assign(line.style, {
    position: "fixed", zIndex: "10000", height: "3px", borderRadius: "2px", pointerEvents: "none", display: "none",
    background: "var(--accent-fill-rest, #0F6CBD)", boxShadow: "0 0 0 1px var(--neutral-layer-1, #FFFFFF)",
  });
  document.body.appendChild(line);
  return line;
}

function showLine(x, y, width) {
  const l = ensureLine();
  l.style.left = Math.round(x) + "px";
  l.style.top = Math.round(y - 1) + "px";
  l.style.width = Math.max(24, Math.round(width)) + "px";
  l.style.display = "block";
}

function hideLine() {
  if (line) line.style.display = "none";
}

function clearDropTargets() {
  document.querySelectorAll(".cr-drop-target").forEach(n => n.classList.remove("cr-drop-target"));
}

function startDrag(e, kind, id, image) {
  drag = { kind, id };
  try {
    e.dataTransfer.effectAllowed = kind === "new" ? "copy" : "move";
    e.dataTransfer.setData("text/plain", kind + ":" + id);
    if (image) e.dataTransfer.setDragImage(image, 16, 16);
  } catch { /* some browsers restrict dataTransfer during synthetic drags */ }
  document.documentElement.classList.add("cr-dragging");
}

function endDrag() {
  drag = null;
  hideLine();
  clearDropTargets();
  document.documentElement.classList.remove("cr-dragging");
  document.querySelectorAll(".cr-is-dragged").forEach(n => n.classList.remove("cr-is-dragged"));
}

/** Index among `items` for pointer y (before the first item whose middle is below y), plus the line position. */
function slot(items, y, container) {
  for (let i = 0; i < items.length; i++) {
    const r = items[i].getBoundingClientRect();
    if (y < r.top + r.height / 2) return { index: i, y: r.top - 2, x: r.left, w: r.width };
  }
  if (items.length > 0) {
    const r = items[items.length - 1].getBoundingClientRect();
    return { index: items.length, y: r.bottom + 2, x: r.left, w: r.width };
  }
  const c = container.getBoundingClientRect();
  return { index: 0, y: c.top + 6, x: c.left + 4, w: c.width - 8 };
}

/** Enter and Space activate div[role=button] elements (they are divs so they can be dragged in every engine). */
function activateOnKey(e) {
  if ((e.key === "Enter" || e.key === " ") && e.target instanceof Element && e.target.matches("[role=button]:not(button)")) {
    e.preventDefault();
    e.target.click();
  }
}

function children(el, selector) {
  return Array.from(el.children).filter(c => c.matches(selector));
}

/** The children matching `childSelector` of every part of the element with that id (parts sit on different sheets), in document order. */
function partsOf(root, partSelector, id, childSelector) {
  const parts = Array.from(root.querySelectorAll(`${partSelector}[data-id="${CSS.escape(id)}"]`));
  return parts.flatMap(p => Array.from(p.querySelectorAll(childSelector)));
}

// ---------------------------------------------------------------- canvas

function canvasTarget(root, e) {
  if (!drag) return null;
  const at = e.target instanceof Element ? e.target : null;
  if (!at || !root.contains(at)) return null;

  if (drag.kind === "module" || drag.kind === "new") {
    // Over a free-flow canvas: the drop lands at the pointer (a page break cannot go there).
    const ff = at.closest(".cr-ff");
    if (ff) {
      if (drag.kind === "new" && drag.id === "pagebreak") return null;
      const p = pointOnCanvas(ff, e.clientX, e.clientY);
      return { el: ff, canvas: ff, sectionId: ff.dataset.section, xPt: p.x, yPt: p.y };
    }
    if (at.closest(".cr-section-ff")) return null;
    let col = at.closest(".cr-col");
    if (!col) {
      // Over a row's gutter or padding: use the nearest column of that row.
      const row = at.closest(".cr-row");
      if (!row) return null;
      const cols = Array.from(row.querySelectorAll(":scope > .cr-grid > .cr-col"));
      let best = null, dist = Infinity;
      for (const c of cols) {
        const r = c.getBoundingClientRect();
        const d = e.clientX < r.left ? r.left - e.clientX : e.clientX > r.right ? e.clientX - r.right : 0;
        if (d < dist) { dist = d; best = c; }
      }
      col = best;
    }
    if (!col) return null;
    // A column the PDF splits across pages is drawn in parts on several sheets; the drop index counts every part.
    const s = slot(partsOf(root, ".cr-col", col.dataset.id, ":scope > .cr-mod"), e.clientY, col);
    return { el: col, targetId: col.dataset.id, ...s };
  }

  if (drag.kind === "row") {
    const section = at.closest(".cr-section");
    if (!section || section.dataset.layout === "freeflow") return null;
    const rows = section.querySelector(":scope > .cr-rows");
    if (!rows) return null;
    const s = slot(partsOf(root, ".cr-section", section.dataset.id, ":scope > .cr-rows > .cr-row"), e.clientY, rows);
    return { el: section, targetId: section.dataset.id, ...s };
  }

  if (drag.kind === "section") {
    // Sections span every page sheet; their document order is their vertical order, so one slot search covers all.
    const body = root.querySelector(".cr-body");
    if (!body) return null;
    const s = slot(Array.from(root.querySelectorAll(".cr-section")), e.clientY, body);
    return { el: null, targetId: "", ...s };
  }
  return null;
}

// ------------------------------------------------------------- free flow
// A free-flow canvas (.cr-ff, data-wpt/data-hpt = size in points) holds items (.cr-item, data-x/y/w/h in points,
// data-lock = keep the aspect). Dragging an item moves it with a live CSS transform; the selected item's overlay
// (.cr-ff-sel) carries eight resize handles. One .NET call (one undo step) on release. Snapping and resizing mirror
// Services/CreatorSnap.cs: edges and centres within 4 screen pixels of the canvas's edges and centre lines or of other
// items' edges and centres snap, with guide lines; Alt turns snapping off; Shift locks the axis while moving and
// inverts the aspect lock while resizing a corner. Items are at least 8 pt.

const SNAP_PX = 4;
const MIN_PT = 8;
const DRAG_START_PX = 3;

function round1(v) { return Math.round(v * 10) / 10; }

function canvasScale(ff) {
  const wpt = Number(ff.dataset.wpt) || 1;
  const r = ff.getBoundingClientRect();
  return { rect: r, scale: r.width / wpt || 1, wpt, hpt: Number(ff.dataset.hpt) || 1 };
}

/** The point (clientX, clientY) in points from the canvas's top-left. */
function pointOnCanvas(ff, clientX, clientY) {
  const c = canvasScale(ff);
  return { x: (clientX - c.rect.left) / c.scale, y: (clientY - c.rect.top) / c.scale };
}

function boxOf(el) {
  return { x: Number(el.dataset.x) || 0, y: Number(el.dataset.y) || 0, w: Number(el.dataset.w) || MIN_PT, h: Number(el.dataset.h) || MIN_PT };
}

function othersOf(ff, id) {
  return Array.from(ff.querySelectorAll(":scope > .cr-item")).filter(i => i.dataset.id !== id).map(boxOf);
}

function targetsX(w, others) {
  const list = [0, w / 2, w];
  for (const o of others) list.push(o.x, o.x + o.w / 2, o.x + o.w);
  return list;
}

function targetsY(h, others) {
  const list = [0, h / 2, h];
  for (const o of others) list.push(o.y, o.y + o.h / 2, o.y + o.h);
  return list;
}

/** Smallest shift putting one of `edges` on one of `targets` within `tol`: { d, g } (g = guide line) or { d: 0, g: null }. */
function snapAxis(edges, targets, tol) {
  let best = Infinity, d = 0, g = null;
  for (const e of edges) {
    for (const t of targets) {
      const dist = Math.abs(t - e);
      if (dist <= tol && dist < best) { best = dist; d = t - e; g = t; }
    }
  }
  return { d, g };
}

/** CreatorSnap.Resize: the box after dragging `handle` by (dx, dy) points. */
function resizeBox(s, handle, dx, dy, keepAspect) {
  const west = handle.includes("w"), east = handle.includes("e"), north = handle.includes("n"), south = handle.includes("s");
  let w = Math.max(MIN_PT, s.w + (east ? dx : west ? -dx : 0));
  let h = Math.max(MIN_PT, s.h + (south ? dy : north ? -dy : 0));
  const corner = (west || east) && (north || south);
  if (keepAspect && corner && s.w > 0 && s.h > 0) {
    const ratio = s.w / s.h;
    if (Math.abs(w / s.w - 1) >= Math.abs(h / s.h - 1)) h = w / ratio; else w = h * ratio;
    if (w < MIN_PT) { w = MIN_PT; h = w / ratio; }
    if (h < MIN_PT) { h = MIN_PT; w = h * ratio; }
  }
  return { x: west ? s.x + s.w - w : s.x, y: north ? s.y + s.h - h : s.y, w, h };
}

function showGuides(g, gx, gy) {
  const ff = g.ff;
  const want = [["v", gx], ["h", gy]];
  for (const [axis, value] of want) {
    let el = ff.querySelector(`:scope > .cr-guide-${axis}`);
    if (value === null || value === undefined) { if (el) el.remove(); continue; }
    if (!el) {
      el = document.createElement("div");
      el.className = `cr-guide cr-guide-${axis}`;
      el.setAttribute("aria-hidden", "true");
      ff.appendChild(el);
    }
    if (axis === "v") el.style.left = value + "pt"; else el.style.top = value + "pt";
  }
}

function clearGuides(ff) {
  ff.querySelectorAll(":scope > .cr-guide").forEach(n => n.remove());
}

function onItemPointerDown(state, e) {
  if (e.button !== 0 || state.gesture) return;
  const t = e.target instanceof Element ? e.target : null;
  if (!t || !state.root.contains(t)) return;
  const handle = t.closest(".cr-ff-sel .cr-handle");
  let item = null, kind = "move";
  if (handle) {
    const sel = handle.closest(".cr-ff-sel");
    item = sel && sel.parentElement ? sel.parentElement.querySelector(`:scope > .cr-item[data-id="${CSS.escape(sel.dataset.for)}"]`) : null;
    kind = "resize";
  } else {
    if (t.closest(".cr-tools, button, a, input, textarea, select")) return;
    item = t.closest(".cr-item");
  }
  const ff = item ? item.closest(".cr-ff") : null;
  if (!item || !ff) return;

  e.preventDefault();   // no text selection, no native image drag
  if (kind === "move") item.focus({ preventScroll: true });
  const id = item.dataset.id;
  const sel = ff.querySelector(`:scope > .cr-ff-sel[data-for="${CSS.escape(id)}"]`);
  state.gesture = {
    kind, handle: handle ? handle.dataset.h : null, item, sel, ff, id,
    start: boxOf(item), box: boxOf(item), sx: e.clientX, sy: e.clientY, pointerId: e.pointerId, active: kind === "resize",
    others: othersOf(ff, id), canvas: canvasScale(ff),
    itemCss: item.getAttribute("style") || "", selCss: sel ? sel.getAttribute("style") || "" : "",
  };
  try { (handle || item).setPointerCapture(e.pointerId); } catch { }
  if (kind === "resize") document.documentElement.classList.add("cr-ff-resizing");
}

function onItemPointerMove(state, e) {
  const g = state.gesture;
  if (!g || e.pointerId !== g.pointerId) return;
  const dxPx = e.clientX - g.sx, dyPx = e.clientY - g.sy;
  if (!g.active) {
    if (Math.hypot(dxPx, dyPx) < DRAG_START_PX) return;
    g.active = true;
    document.documentElement.classList.add("cr-ff-moving");
  }
  g.item.classList.add("is-moving");
  const s = g.canvas.scale;
  const tol = SNAP_PX / s;
  let dx = dxPx / s, dy = dyPx / s;

  if (g.kind === "move") {
    let lockX = false, lockY = false;
    if (e.shiftKey) { if (Math.abs(dx) >= Math.abs(dy)) { dy = 0; lockY = true; } else { dx = 0; lockX = true; } }
    let box = { x: g.start.x + dx, y: g.start.y + dy, w: g.start.w, h: g.start.h };
    let gx = null, gy = null;
    if (!e.altKey) {
      if (!lockX) {
        const r = snapAxis([box.x, box.x + box.w / 2, box.x + box.w], targetsX(g.canvas.wpt, g.others), tol);
        box.x += r.d; gx = r.g;
      }
      if (!lockY) {
        const r = snapAxis([box.y, box.y + box.h / 2, box.y + box.h], targetsY(g.canvas.hpt, g.others), tol);
        box.y += r.d; gy = r.g;
      }
    }
    box.x = round1(box.x); box.y = round1(box.y);
    g.box = box;
    // The stage is zoomed: a transform in CSS px is scaled too, so divide the screen distance by the zoom.
    const zoom = stageZoom(g.ff);
    const t2 = `translate(${(box.x - g.start.x) * s / zoom}px, ${(box.y - g.start.y) * s / zoom}px)`;
    g.item.style.transform = t2;
    if (g.sel) g.sel.style.transform = t2;
    showGuides(g, gx, gy);
    return;
  }

  // Resize: corners keep the aspect when the item says so; Shift inverts that.
  const corner = g.handle.length === 2;
  const keep = corner && ((g.item.dataset.lock === "1") !== e.shiftKey);
  let box = resizeBox(g.start, g.handle, dx, dy, keep);
  let gx = null, gy = null;
  if (!e.altKey && !keep) {
    if (g.handle.includes("e") || g.handle.includes("w")) {
      const west = g.handle.includes("w");
      const r = snapAxis([west ? box.x : box.x + box.w], targetsX(g.canvas.wpt, g.others), tol);
      if (r.g !== null && box.w + (west ? -r.d : r.d) >= MIN_PT) {
        if (west) { box.x += r.d; box.w -= r.d; } else { box.w += r.d; }
        gx = r.g;
      }
    }
    if (g.handle.includes("n") || g.handle.includes("s")) {
      const north = g.handle.includes("n");
      const r = snapAxis([north ? box.y : box.y + box.h], targetsY(g.canvas.hpt, g.others), tol);
      if (r.g !== null && box.h + (north ? -r.d : r.d) >= MIN_PT) {
        if (north) { box.y += r.d; box.h -= r.d; } else { box.h += r.d; }
        gy = r.g;
      }
    }
  }
  box = { x: round1(box.x), y: round1(box.y), w: round1(Math.max(MIN_PT, box.w)), h: round1(Math.max(MIN_PT, box.h)) };
  g.box = box;
  for (const el of [g.item, g.sel]) {
    if (!el) continue;
    el.style.left = box.x + "pt"; el.style.top = box.y + "pt"; el.style.width = box.w + "pt"; el.style.height = box.h + "pt";
  }
  const label = g.sel ? g.sel.querySelector(".cr-tools-name") : null;
  if (label) label.dataset.live = `${box.w} × ${box.h} pt`;
  showGuides(g, gx, gy);
}

function stageZoom(el) {
  const stage = el.closest(".cr-stage");
  const z = stage ? Number(stage.style.zoom) : 1;
  return z > 0 ? z : 1;
}

function onItemPointerUp(state, e, cancelled) {
  const g = state.gesture;
  if (!g || e.pointerId !== g.pointerId) return;
  state.gesture = null;
  try { (g.item).releasePointerCapture(e.pointerId); } catch { }
  document.documentElement.classList.remove("cr-ff-moving", "cr-ff-resizing");
  clearGuides(g.ff);
  const moved = g.active && (g.box.x !== g.start.x || g.box.y !== g.start.y || g.box.w !== g.start.w || g.box.h !== g.start.h);
  if (cancelled || !moved) {
    restore(g);
    return;
  }

  // Keep the live position until the circuit re-renders the item there (or give up and restore after a moment).
  const expect = g.box;
  if (g.kind === "move") {
    state.dotnet.invokeMethodAsync("OnItemMoved", g.id, expect.x, expect.y).catch(() => restore(g));
  } else {
    state.dotnet.invokeMethodAsync("OnItemResized", g.id, expect.x, expect.y, expect.w, expect.h).catch(() => restore(g));
  }
  settle(g, expect);
}

function near(a, b) { return Math.abs(a - b) < 0.06; }

function settle(g, expect) {
  const item = g.item;
  const matches = () => {
    const b = boxOf(item);
    return near(b.x, expect.x) && near(b.y, expect.y) && near(b.w, expect.w) && near(b.h, expect.h);
  };
  let finished = false;
  const done = ok => {
    if (finished) return;
    finished = true;
    observer.disconnect();
    clearTimeout(timer);
    if (ok) {
      item.style.transform = "";
      item.classList.remove("is-moving");
      const sel = item.parentElement ? item.parentElement.querySelector(`:scope > .cr-ff-sel[data-for="${CSS.escape(g.id)}"]`) : null;
      if (sel) sel.style.transform = "";
    } else {
      restore(g);
    }
  };
  const observer = new MutationObserver(() => { if (matches()) done(true); });
  observer.observe(item, { attributes: true, attributeFilter: ["data-x", "data-y", "data-w", "data-h", "style"] });
  const timer = setTimeout(() => done(matches()), 1500);
  if (matches()) done(true);
}

function restore(g) {
  g.item.setAttribute("style", g.itemCss);
  g.item.classList.remove("is-moving");
  if (g.sel && g.sel.isConnected) g.sel.setAttribute("style", g.selCss);
}

export function initCanvas(root, dotnet) {
  if (!root || states.has(root)) return;
  const state = { root, dotnet, hover: null, zoom: "fit", pageWidthPx: 794, resize: null, pending: null };

  state.onOver = e => {
    const el = e.target instanceof Element ? e.target.closest("[data-kind]") : null;
    const hit = el && root.contains(el) ? el : null;
    if (state.hover === hit) return;
    if (state.hover) state.hover.classList.remove("is-hover");
    state.hover = hit;
    if (hit) hit.classList.add("is-hover");
  };
  state.onLeave = () => {
    if (state.hover) state.hover.classList.remove("is-hover");
    state.hover = null;
  };
  state.onClick = e => {
    // The canvas is not a browser: links never navigate.
    if (e.target instanceof Element && e.target.closest(".aio-doc a")) e.preventDefault();
  };
  state.onKey = e => {
    if ((e.key === "Enter" || e.key === " ") && e.target instanceof Element && e.target.matches("[data-kind]")) {
      e.preventDefault();
      e.target.click();
    }
  };
  state.onDragStart = e => {
    const el = e.target instanceof Element ? e.target.closest("[data-drag]") : null;
    if (!el || !root.contains(el)) return;
    const kind = el.dataset.drag;
    const id = el.dataset.id;
    const image = kind === "module" ? el : root.querySelector(`[data-kind="${kind}"][data-id="${id}"]`);
    startDrag(e, kind, id, image);
    if (image) requestAnimationFrame(() => image.classList.add("cr-is-dragged"));
    e.stopPropagation();
  };
  state.onDragOver = e => {
    const t = canvasTarget(root, e);
    clearDropTargets();
    if (!t) { hideLine(); state.pending = null; return; }
    e.preventDefault();
    try { e.dataTransfer.dropEffect = drag.kind === "new" ? "copy" : "move"; } catch { }
    if (t.el) t.el.classList.add("cr-drop-target");
    if (t.canvas) hideLine(); else showLine(t.x, t.y, t.w);
    state.pending = t;
  };
  state.onDragLeave = e => {
    if (e.relatedTarget instanceof Node && root.contains(e.relatedTarget)) return;
    hideLine();
    clearDropTargets();
  };
  state.onDrop = e => {
    let t = state.pending || canvasTarget(root, e);
    const d = drag;
    if (t && t.canvas) t = canvasTarget(root, e) || t;   // the exact drop point, not the last dragover's
    e.preventDefault();
    endDrag();
    state.pending = null;
    if (!t || !d) return;
    if (t.canvas) {
      dotnet.invokeMethodAsync("OnCanvasPlace", d.kind, d.id, t.sectionId, round1(t.xPt), round1(t.yPt)).catch(() => { });
      return;
    }
    dotnet.invokeMethodAsync("OnCanvasDrop", d.kind, d.id, t.targetId || "", t.index).catch(() => { });
  };

  // Free-flow items: pointer drags to move and resize (not HTML5 drag and drop).
  state.gesture = null;
  state.onPointerDown = e => onItemPointerDown(state, e);
  state.onPointerMove = e => onItemPointerMove(state, e);
  state.onPointerUp = e => onItemPointerUp(state, e, false);
  state.onPointerCancel = e => onItemPointerUp(state, e, true);
  root.addEventListener("pointerdown", state.onPointerDown);
  root.addEventListener("pointermove", state.onPointerMove);
  root.addEventListener("pointerup", state.onPointerUp);
  root.addEventListener("pointercancel", state.onPointerCancel);

  root.addEventListener("mouseover", state.onOver);
  root.addEventListener("mouseleave", state.onLeave);
  root.addEventListener("click", state.onClick, true);
  root.addEventListener("keydown", state.onKey);
  root.addEventListener("dragstart", state.onDragStart);
  root.addEventListener("dragover", state.onDragOver);
  root.addEventListener("dragleave", state.onDragLeave);
  root.addEventListener("drop", state.onDrop);
  root.addEventListener("dragend", endDrag);

  state.resize = new ResizeObserver(() => applyZoom(state));
  state.resize.observe(root);
  states.set(root, state);
  applyZoom(state);
}

function applyZoom(state) {
  const stage = state.root.querySelector(".cr-stage");
  if (!stage) return;
  let z;
  if (state.zoom === "fit") {
    const available = state.root.clientWidth - 88;
    z = Math.max(0.3, Math.min(1.25, available / Math.max(100, state.pageWidthPx)));
  } else {
    z = Number(state.zoom) || 1;
  }
  stage.style.zoom = String(Math.round(z * 1000) / 1000);
}

export function setZoom(root, zoom, pageWidthPx) {
  const state = states.get(root);
  if (!state) return;
  state.zoom = zoom || "fit";
  state.pageWidthPx = pageWidthPx || 794;
  applyZoom(state);
}

/** Moves keyboard focus to the canvas element (or tree node) with that id, scrolling it into view. */
export function focusId(root, id) {
  if (!root || !id) return;
  const el = root.querySelector(`[data-id="${CSS.escape(id)}"][tabindex]`);
  if (!el) return;
  el.focus({ preventScroll: true });
  el.scrollIntoView({ block: "nearest", behavior: "smooth" });
}

/** Scrolls the element with that id into view without moving focus. */
export function revealId(root, id) {
  if (!root || !id) return;
  const el = root.querySelector(`[data-id="${CSS.escape(id)}"]`);
  if (el) el.scrollIntoView({ block: "nearest", behavior: "smooth" });
}

// ------------------------------------------------------------ insert tiles

export function initPalette(root) {
  if (!root || states.has(root)) return;
  const state = {
    onDragStart: e => {
      const tile = e.target instanceof Element ? e.target.closest("[data-insert]") : null;
      if (!tile) return;
      startDrag(e, "new", tile.dataset.insert, tile);
    },
  };
  root.addEventListener("dragstart", state.onDragStart);
  root.addEventListener("dragend", endDrag);
  root.addEventListener("keydown", activateOnKey);
  state.dispose = () => {
    root.removeEventListener("dragstart", state.onDragStart);
    root.removeEventListener("dragend", endDrag);
    root.removeEventListener("keydown", activateOnKey);
  };
  states.set(root, state);
}

// --------------------------------------------------------- structure tree

function treeTarget(root, e) {
  if (!drag) return null;
  const node = e.target instanceof Element ? e.target.closest(".cr-tnode") : null;
  if (!node || !root.contains(node)) return null;
  const r = node.getBoundingClientRect();
  const after = e.clientY > r.top + r.height / 2;
  const kind = node.dataset.tkind;
  const index = Number(node.dataset.index || 0);
  const count = Number(node.dataset.count || 0);
  const lineAt = after ? r.bottom + 1 : r.top - 1;

  if (drag.kind === "module" || drag.kind === "new" || drag.kind === "item") {
    // Free-flow items are listed front to back: dropping between them sets the stacking order.
    const canvasTarget = kind === "item" || (kind === "section" && node.dataset.layout === "freeflow");
    if (canvasTarget && drag.kind === "new" && drag.id === "pagebreak") return null;
    if (kind === "item") return { targetId: node.dataset.parent, index: index + (after ? 1 : 0), x: r.left, y: lineAt, w: r.width, el: node };
    if (canvasTarget) return { targetId: node.dataset.id, index: 0, x: r.left + 16, y: r.bottom + 1, w: r.width - 16, el: node };
    if (kind === "module") return { targetId: node.dataset.parent, index: index + (after ? 1 : 0), x: r.left, y: lineAt, w: r.width, el: node };
    if (kind === "column") return { targetId: node.dataset.id, index: count, x: r.left + 16, y: r.bottom + 1, w: r.width - 16, el: node };
    return null;
  }
  if (drag.kind === "row") {
    if (kind === "row") return { targetId: node.dataset.parent, index: index + (after ? 1 : 0), x: r.left, y: lineAt, w: r.width, el: node };
    if (kind === "section" && node.dataset.layout !== "freeflow") return { targetId: node.dataset.id, index: count, x: r.left + 16, y: r.bottom + 1, w: r.width - 16, el: node };
    return null;
  }
  if (drag.kind === "section" && kind === "section") {
    return { targetId: "", index: index + (after ? 1 : 0), x: r.left, y: lineAt, w: r.width, el: node };
  }
  return null;
}

export function initTree(root, dotnet) {
  if (!root || states.has(root)) return;
  const state = { pending: null };
  state.onDragStart = e => {
    const node = e.target instanceof Element ? e.target.closest(".cr-tnode[draggable=true]") : null;
    if (!node) return;
    startDrag(e, node.dataset.tkind, node.dataset.id, node);
  };
  state.onDragOver = e => {
    const t = treeTarget(root, e);
    clearDropTargets();
    if (!t) { hideLine(); state.pending = null; return; }
    e.preventDefault();
    try { e.dataTransfer.dropEffect = drag.kind === "new" ? "copy" : "move"; } catch { }
    showLine(t.x, t.y, t.w);
    state.pending = t;
  };
  state.onDragLeave = e => {
    if (e.relatedTarget instanceof Node && root.contains(e.relatedTarget)) return;
    hideLine();
  };
  state.onDrop = e => {
    const t = state.pending || treeTarget(root, e);
    const d = drag;
    e.preventDefault();
    endDrag();
    state.pending = null;
    if (!t || !d) return;
    dotnet.invokeMethodAsync("OnTreeDrop", d.kind, d.id, t.targetId || "", t.index).catch(() => { });
  };
  root.addEventListener("dragstart", state.onDragStart);
  root.addEventListener("dragover", state.onDragOver);
  root.addEventListener("dragleave", state.onDragLeave);
  root.addEventListener("drop", state.onDrop);
  root.addEventListener("dragend", endDrag);
  root.addEventListener("keydown", activateOnKey);
  state.dispose = () => {
    root.removeEventListener("keydown", activateOnKey);
    root.removeEventListener("dragstart", state.onDragStart);
    root.removeEventListener("dragover", state.onDragOver);
    root.removeEventListener("dragleave", state.onDragLeave);
    root.removeEventListener("drop", state.onDrop);
    root.removeEventListener("dragend", endDrag);
  };
  states.set(root, state);
}

export function dispose(root) {
  const state = states.get(root);
  if (!state) return;
  if (state.dispose) {
    state.dispose();
  } else {
    root.removeEventListener("mouseover", state.onOver);
    root.removeEventListener("mouseleave", state.onLeave);
    root.removeEventListener("click", state.onClick, true);
    root.removeEventListener("keydown", state.onKey);
    root.removeEventListener("dragstart", state.onDragStart);
    root.removeEventListener("dragover", state.onDragOver);
    root.removeEventListener("dragleave", state.onDragLeave);
    root.removeEventListener("drop", state.onDrop);
    root.removeEventListener("dragend", endDrag);
    root.removeEventListener("pointerdown", state.onPointerDown);
    root.removeEventListener("pointermove", state.onPointerMove);
    root.removeEventListener("pointerup", state.onPointerUp);
    root.removeEventListener("pointercancel", state.onPointerCancel);
    state.resize?.disconnect();
  }
  states.delete(root);
  if (!document.querySelector(".cr-canvas, .cr-tree")) {
    endDrag();
    line?.remove();
    line = null;
  }
}
