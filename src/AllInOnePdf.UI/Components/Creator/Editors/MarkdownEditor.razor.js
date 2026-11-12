// Markdown formatting for the Creator's text editors. Edits go through execCommand("insertText") where available so
// the browser's own undo (inside the field) keeps working, and always end with an "input" event for Blazor.

function replaceSelection(el, text, selectFrom, selectTo) {
  el.focus();
  const start = el.selectionStart;
  let ok = false;
  try { ok = document.execCommand("insertText", false, text); } catch { ok = false; }
  if (!ok) {
    el.setRangeText(text, el.selectionStart, el.selectionEnd, "end");
    el.dispatchEvent(new Event("input", { bubbles: true }));
  }
  if (selectFrom !== undefined) el.setSelectionRange(start + selectFrom, start + selectTo);
}

function wrap(el, before, after, placeholder) {
  const s = el.selectionStart, e = el.selectionEnd;
  const selected = el.value.slice(s, e) || placeholder;
  replaceSelection(el, before + selected + after, before.length, before.length + selected.length);
}

function prefixLines(el, prefix) {
  const v = el.value;
  let s = el.selectionStart, e = el.selectionEnd;
  s = v.lastIndexOf("\n", s - 1) + 1;
  const nl = v.indexOf("\n", e);
  e = nl < 0 ? v.length : nl;
  el.setSelectionRange(s, e);
  const lines = v.slice(s, e).split("\n");
  const all = lines.every(l => l.startsWith(prefix));
  const text = lines.map(l => all ? l.slice(prefix.length) : prefix + l).join("\n");
  replaceSelection(el, text, 0, text.length);
}

export function format(el, action) {
  if (!el) return;
  switch (action) {
    case "bold": wrap(el, "**", "**", "bold text"); break;
    case "italic": wrap(el, "*", "*", "italic text"); break;
    case "code": wrap(el, "`", "`", "code"); break;
    case "link": {
      const s = el.selectionStart, e = el.selectionEnd;
      const label = el.value.slice(s, e) || "link text";
      const text = "[" + label + "](https://)";
      replaceSelection(el, text, label.length + 3, label.length + 11);
      break;
    }
    case "bullet": prefixLines(el, "- "); break;
    case "heading": prefixLines(el, "## "); break;
    case "quote": prefixLines(el, "> "); break;
  }
}

export function init(wrapEl, markdown) {
  if (!wrapEl || !markdown) return;
  wrapEl.addEventListener("keydown", e => {
    const el = e.target;
    if (!(el instanceof HTMLTextAreaElement) || !(e.ctrlKey || e.metaKey) || e.altKey) return;
    const key = e.key.toLowerCase();
    if (key === "b") { e.preventDefault(); format(el, "bold"); }
    else if (key === "i") { e.preventDefault(); format(el, "italic"); }
  });
}
