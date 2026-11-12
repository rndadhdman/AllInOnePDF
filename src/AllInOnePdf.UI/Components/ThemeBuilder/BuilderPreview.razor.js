// Click-to-select for the theme builder's fast preview.
// Maps aio-* / tb-* classes to a settings group, outlines the hovered and selected element and reports
// the selection to .NET. Selection survives re-renders (the preview HTML is replaced on every theme change).

const TARGETS = [
  { sel: "a.aio-link", group: "links", label: () => "Link" },
  { sel: "code.aio-code", group: "code", label: () => "Inline code" },
  { sel: ".tb-header", group: "headerfooter", sub: "header", label: () => "Header" },
  { sel: ".tb-footer", group: "headerfooter", sub: "footer", label: () => "Footer" },
  { sel: ".tb-toc", group: "toc", label: () => "Table of contents" },
  { sel: ".tb-cover", group: "cover", label: () => "Cover" },
  { sel: ".aio-codeblock, .aio-math", group: "code", label: () => "Code block" },
  { sel: ".aio-tablewrap", group: "tables", label: () => "Table" },
  { sel: ".aio-figure", group: "images", label: () => "Image" },
  { sel: ".aio-h", group: "headings", sub: el => headingLevel(el), label: el => "Heading " + headingLevel(el).slice(1) },
  { sel: ".aio-list", group: "lists", label: el => el.tagName === "OL" ? "Numbered list" : (el.classList.contains("aio-tasklist") ? "Task list" : "List") },
  { sel: ".aio-callout", group: "callouts", sub: el => calloutKind(el), label: el => calloutLabel(calloutKind(el)) },
  { sel: ".aio-quote", group: "quotes", label: () => "Quote" },
  { sel: ".aio-footnotes", group: "typography", label: () => "Footnotes" },
  { sel: ".aio-hr", group: "colors", label: () => "Divider" },
  { sel: ".aio-p", group: "typography", label: () => "Paragraph" },
  { sel: ".tb-sheet", group: "page", label: () => "Page" },
];

const states = new WeakMap();

function headingLevel(el) {
  for (let i = 1; i <= 6; i++) {
    if (el.classList.contains("aio-h" + i)) return "h" + i;
  }
  return "h1";
}

const CALLOUT_KINDS = ["note", "tip", "important", "warning", "caution"];

function calloutKind(el) {
  return CALLOUT_KINDS.find(k => el.classList.contains("aio-callout-" + k)) ?? "note";
}

function calloutLabel(kind) {
  return kind.charAt(0).toUpperCase() + kind.slice(1) + " callout";
}

function resolve(root, start) {
  if (!(start instanceof Element)) return null;
  for (const t of TARGETS) {
    const el = start.closest(t.sel);
    if (el && root.contains(el)) {
      const sub = typeof t.sub === "function" ? t.sub(el) : (t.sub ?? null);
      return { el, target: t, sub, label: t.label(el) };
    }
  }
  return null;
}

function stageOf(root) {
  return root.querySelector(".tb-stage");
}

function offsetWithin(el, stage) {
  let x = 0, y = 0, node = el;
  while (node && node !== stage) {
    x += node.offsetLeft;
    y += node.offsetTop;
    node = node.offsetParent;
    if (node && !stage.contains(node)) break;
  }
  return { x, y };
}

function placeLabel(state) {
  const stage = stageOf(state.root);
  const overlay = state.root.querySelector(".tb-overlay");
  if (!overlay) return;
  overlay.replaceChildren();
  if (!state.selected || !state.selected.el || !stage || !stage.contains(state.selected.el)) return;
  const { x, y } = offsetWithin(state.selected.el, stage);
  const tag = document.createElement("div");
  tag.className = "tb-sel-label";
  tag.textContent = state.selected.label;
  tag.style.left = Math.max(0, x) + "px";
  tag.style.top = Math.max(0, y - 22) + "px";
  overlay.appendChild(tag);
}

function clearClass(root, cls) {
  root.querySelectorAll("." + cls).forEach(n => n.classList.remove(cls));
}

function applySelection(state) {
  clearClass(state.root, "tb-selected");
  const s = state.selected;
  if (!s) { placeLabel(state); return; }
  if (!s.el || !state.root.contains(s.el)) {
    // Re-find after the markup was replaced: same selector, same position in document order.
    const all = state.root.querySelectorAll(s.target.sel);
    s.el = all[Math.min(s.index, all.length - 1)] || null;
  }
  if (s.el) s.el.classList.add("tb-selected");
  placeLabel(state);
}

function select(state, hit, notify) {
  const all = Array.from(state.root.querySelectorAll(hit.target.sel));
  state.selected = { el: hit.el, target: hit.target, index: Math.max(0, all.indexOf(hit.el)), label: hit.label, sub: hit.sub };
  applySelection(state);
  if (notify && state.dotnet) {
    state.dotnet.invokeMethodAsync("OnPreviewSelected", hit.target.group, hit.sub, hit.label).catch(() => { });
  }
}

export function init(root, dotnet) {
  if (!root || states.has(root)) return;
  const state = { root, dotnet, selected: null, hover: null, zoom: "fit", pageWidthPx: 794, observer: null, resize: null, raf: 0 };

  state.onClick = e => {
    const hit = resolve(root, e.target);
    if (e.target.closest && e.target.closest("a")) e.preventDefault();
    if (!hit) return;
    e.preventDefault();
    select(state, hit, true);
  };
  state.onOver = e => {
    const hit = resolve(root, e.target);
    const el = hit ? hit.el : null;
    if (state.hover === el) return;
    if (state.hover) state.hover.classList.remove("tb-hover");
    state.hover = el;
    if (el) {
      el.classList.add("tb-hover");
      el.setAttribute("title", hit.label + " · click to edit");
    }
  };
  state.onLeave = () => {
    if (state.hover) state.hover.classList.remove("tb-hover");
    state.hover = null;
  };
  state.onKey = e => {
    if (e.key === "Escape" && state.selected) {
      state.selected = null;
      applySelection(state);
    }
  };

  root.addEventListener("click", state.onClick);
  root.addEventListener("mouseover", state.onOver);
  root.addEventListener("mouseleave", state.onLeave);
  root.addEventListener("keydown", state.onKey);

  state.observer = new MutationObserver(() => {
    cancelAnimationFrame(state.raf);
    state.raf = requestAnimationFrame(() => {
      state.hover = null;
      applySelection(state);
      applyZoom(state);
    });
  });
  const stage = stageOf(root);
  if (stage) state.observer.observe(stage, { childList: true, subtree: false });
  // The generated <style> lives directly under the root: a theme-only change replaces it and moves things around.
  state.observer.observe(root, { childList: true, subtree: false });

  state.resize = new ResizeObserver(() => applyZoom(state));
  state.resize.observe(root);

  states.set(root, state);
  applyZoom(state);
}

function applyZoom(state) {
  const stage = stageOf(state.root);
  if (!stage) return;
  let z;
  if (state.zoom === "fit") {
    const available = state.root.clientWidth - 48;
    z = Math.max(0.2, Math.min(1.5, available / Math.max(100, state.pageWidthPx)));
  } else {
    z = Number(state.zoom) || 1;
  }
  stage.style.zoom = String(Math.round(z * 1000) / 1000);
  placeLabel(state);
}

export function setZoom(root, zoom, pageWidthPx) {
  const state = states.get(root);
  if (!state) return;
  state.zoom = zoom || "fit";
  state.pageWidthPx = pageWidthPx || 794;
  applyZoom(state);
}

export function clearSelection(root) {
  const state = states.get(root);
  if (!state) return;
  state.selected = null;
  applySelection(state);
}

export function dispose(root) {
  const state = states.get(root);
  if (!state) return;
  root.removeEventListener("click", state.onClick);
  root.removeEventListener("mouseover", state.onOver);
  root.removeEventListener("mouseleave", state.onLeave);
  root.removeEventListener("keydown", state.onKey);
  state.observer?.disconnect();
  state.resize?.disconnect();
  cancelAnimationFrame(state.raf);
  state.dotnet = null;
  states.delete(root);
}
