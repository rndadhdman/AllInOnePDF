// Theme builder page helpers: Ctrl+Z / Ctrl+Y / Ctrl+S shortcuts, a "leave page?" prompt while there are unsaved
// changes, and a narrow-window flag for the top bar.

let dotnet = null;
let dirty = false;

function isTextEditing(e) {
  const el = (e.composedPath && e.composedPath()[0]) || e.target;
  if (!el || !el.tagName) return false;
  const tag = el.tagName.toUpperCase();
  if (tag === "TEXTAREA") return true;
  if (tag === "INPUT") {
    const type = (el.type || "text").toLowerCase();
    return ["text", "search", "number", "email", "url", "tel", "password"].includes(type);
  }
  return el.isContentEditable === true;
}

function onKeyDown(e) {
  if (!dotnet || !(e.ctrlKey || e.metaKey) || e.altKey) return;
  const key = e.key.toLowerCase();
  let action = null;
  if (key === "s") action = "save";
  else if (key === "z" && !e.shiftKey) action = "undo";
  else if (key === "y" || (key === "z" && e.shiftKey)) action = "redo";
  if (!action) return;
  // Inside a text field, let the browser undo the typing itself.
  if (action !== "save" && isTextEditing(e)) return;
  e.preventDefault();
  dotnet.invokeMethodAsync("OnShortcut", action).catch(() => { });
}

function onBeforeUnload(e) {
  if (!dirty) return;
  e.preventDefault();
  e.returnValue = "";
}

// Below this width the top bar folds Reset / Export / Save as… into a "More" menu (keep in sync with the CSS).
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

/** Keeps --tb-bar-h on the main area equal to the preview bar's height, so the narrow settings drawer opens below it. */
let barObserver = null;
export function observeBar(bar) {
  if (barObserver) barObserver.disconnect();
  barObserver = null;
  const main = bar && bar.closest(".tb-main");
  if (!main || typeof ResizeObserver === "undefined") return;
  barObserver = new ResizeObserver(() => main.style.setProperty("--tb-bar-h", bar.offsetHeight + "px"));
  barObserver.observe(bar);
}

export function setDirty(value) {
  dirty = !!value;
}

export function dispose() {
  document.removeEventListener("keydown", onKeyDown, true);
  window.removeEventListener("beforeunload", onBeforeUnload);
  if (narrowMql) narrowMql.removeEventListener("change", onNarrowChange);
  narrowMql = null;
  if (barObserver) barObserver.disconnect();
  barObserver = null;
  dotnet = null;
  dirty = false;
}
