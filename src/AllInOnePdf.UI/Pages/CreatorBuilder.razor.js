// Creator builder page helpers: keyboard shortcuts (Ctrl+Z / Ctrl+Y / Ctrl+S, Delete, Ctrl+D, Alt+Up/Down, Escape;
// for a selected free-flow item also the arrows (nudge 1 pt, Shift 10 pt) and Ctrl+] / Ctrl+[ (+Shift) for stacking),
// a "leave page?" prompt while there are unsaved changes, and a narrow-window flag for the drawers.

let dotnet = null;
let dirty = false;

function targetOf(e) {
  return (e.composedPath && e.composedPath()[0]) || e.target;
}

function isTextEditing(e) {
  const el = targetOf(e);
  if (!el || !el.tagName) return false;
  const tag = el.tagName.toUpperCase();
  if (tag === "TEXTAREA" || tag === "SELECT") return true;
  if (tag === "INPUT") {
    const type = (el.type || "text").toLowerCase();
    return ["text", "search", "number", "email", "url", "tel", "password", "date"].includes(type);
  }
  return el.isContentEditable === true;
}

function inOverlay(e) {
  const el = targetOf(e);
  if (!(el instanceof Element)) return false;
  return !!el.closest("fluent-dialog, .fluent-dialog, fluent-menu, [role=menu], [role=dialog], [role=listbox]");
}

function isHidden(el) {
  if (!(el instanceof Element) || el === document.body) return false;
  const panel = el.closest(".cr-left, .cr-right");
  return !!panel && getComputedStyle(panel).visibility === "hidden";
}

/** True when a free-flow item is selected on the design canvas. */
function selectedItem() {
  return !!document.querySelector(".cr-canvas .cr-item.is-selected");
}

function send(e, action) {
  e.preventDefault();
  e.stopPropagation();
  dotnet.invokeMethodAsync("OnShortcut", action).catch(() => { });
}

function onKeyDown(e) {
  if (!dotnet) return;
  const key = e.key;
  const lower = key.toLowerCase();
  const ctrl = e.ctrlKey || e.metaKey;

  if (ctrl && !e.altKey) {
    if (lower === "s") { send(e, "save"); return; }
    if (isTextEditing(e) || inOverlay(e)) return;   // let fields undo their own typing
    if (lower === "z" && !e.shiftKey) { send(e, "undo"); return; }
    if (lower === "y" || (lower === "z" && e.shiftKey)) { send(e, "redo"); return; }
    if (lower === "d") { send(e, "duplicate"); return; }
    // Stacking order of a free-flow item: Ctrl+] / Ctrl+[ one step, with Shift to the front / back.
    if ((e.code === "BracketRight" || key === "]" || key === "}") && selectedItem()) { send(e, e.shiftKey ? "front" : "forward"); return; }
    if ((e.code === "BracketLeft" || key === "[" || key === "{") && selectedItem()) { send(e, e.shiftKey ? "back" : "backward"); return; }
    return;
  }

  // Arrow keys nudge a selected free-flow item by 1 pt (Shift: 10 pt) while the canvas or nothing has focus.
  if (!e.altKey && key.startsWith("Arrow") && !isTextEditing(e) && !inOverlay(e) && selectedItem()) {
    const el = targetOf(e);
    if (!(el instanceof Element) || el === document.body || el.closest(".cr-canvas")) {
      const step = e.shiftKey ? 10 : 1;
      const dx = key === "ArrowLeft" ? -step : key === "ArrowRight" ? step : 0;
      const dy = key === "ArrowUp" ? -step : key === "ArrowDown" ? step : 0;
      if (dx !== 0 || dy !== 0) { send(e, `nudge:${dx}:${dy}`); return; }
    }
  }

  // Escape from a field in a drawer that has just closed (focus stays in the hidden panel) still clears the selection.
  if (key === "Escape" && !e.altKey && !ctrl && isHidden(targetOf(e))) { send(e, "escape"); return; }
  if (isTextEditing(e) || inOverlay(e)) return;
  if (e.altKey && !ctrl && (key === "ArrowUp" || key === "ArrowDown")) { send(e, key === "ArrowUp" ? "up" : "down"); return; }
  if (!e.altKey && !ctrl && (key === "Delete" || key === "Backspace")) {
    // Only when the canvas, the tree or nothing in particular has focus (never while a button in the inspector does).
    const el = targetOf(e);
    if (el instanceof Element && el !== document.body && !el.closest(".cr-canvas, .cr-tree")) return;
    send(e, "delete");
    return;
  }
  if (key === "Escape" && !e.altKey && !ctrl) send(e, "escape");
}

function onBeforeUnload(e) {
  if (!dirty) return;
  e.preventDefault();
  e.returnValue = "";
}

const narrowQuery = "(max-width: 1100px)";
let narrowMql = null;

function onNarrowChange(e) {
  if (dotnet) dotnet.invokeMethodAsync("OnNarrowChanged", e.matches).catch(() => { });
}

/** Wires shortcuts and the breakpoint listener; returns whether the window is currently narrow. */
export function init(ref) {
  dotnet = ref;
  document.addEventListener("keydown", onKeyDown, true);
  window.addEventListener("beforeunload", onBeforeUnload);
  narrowMql = window.matchMedia(narrowQuery);
  narrowMql.addEventListener("change", onNarrowChange);
  return narrowMql.matches;
}

export function setDirty(value) {
  dirty = !!value;
}

export function dispose() {
  document.removeEventListener("keydown", onKeyDown, true);
  window.removeEventListener("beforeunload", onBeforeUnload);
  if (narrowMql) narrowMql.removeEventListener("change", onNarrowChange);
  narrowMql = null;
  dotnet = null;
  dirty = false;
}
