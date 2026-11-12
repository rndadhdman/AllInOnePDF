// Reader page: keyboard shortcuts and focus-mode idle detection. (App dark mode comes from ThemeService in C#.)
// Loaded by Reader.razor via import(); every listener is removed in dispose().

const EDITABLE = [
  "input", "textarea", "select", "[contenteditable='']", "[contenteditable='true']",
  "fluent-text-field", "fluent-text-area", "fluent-number-field", "fluent-search", "fluent-combobox",
  "fluent-select", "fluent-slider", "fluent-listbox", "fluent-menu", "fluent-radio-group",
  "[role='slider']", "[role='textbox']", "[role='combobox']", "[role='listbox']", "[role='menu']",
  "[role='menuitem']", "[role='spinbutton']",
].join(",");

const IDLE_MS = 2500;
const LINE_PX = 64;

function isEditable(target) {
  if (!(target instanceof Element)) return false;
  if (target.isContentEditable) return true;
  return target.closest(EDITABLE) !== null;
}


class ReaderPage {
  constructor(root, dotnet) {
    this.root = root;
    this.dotnet = dotnet;
    this.focus = false;
    this.idleTimer = 0;
    this.reduce = window.matchMedia("(prefers-reduced-motion: reduce)");

    this.lastInput = "pointer";
    this.onKey = (e) => {
      this.lastInput = "key";
      this.handleKey(e);
    };
    this.onWake = (e) => {
      if (e && e.type && e.type.startsWith("pointer") || e?.type === "touchstart") this.lastInput = "pointer";
      this.wake();
    };
    document.addEventListener("keydown", this.onKey);
    root.addEventListener("pointermove", this.onWake, { passive: true });
    root.addEventListener("pointerdown", this.onWake, { passive: true });
    root.addEventListener("touchstart", this.onWake, { passive: true });
    root.addEventListener("focusin", this.onWake);
  }

  invoke(method, ...args) {
    try {
      this.dotnet.invokeMethodAsync(method, ...args).catch(() => { });
    } catch {
      // circuit gone
    }
  }

  scroller() {
    return this.root.querySelector("[data-reader-scroll]");
  }

  scrollBy(dy) {
    const s = this.scroller();
    if (!s) return;
    s.scrollBy({ top: dy, behavior: this.reduce.matches ? "auto" : "smooth" });
  }

  handleKey(e) {
    if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey || e.isComposing) return;
    if (!this.root.isConnected) return;
    const target = e.target;
    if (isEditable(target)) return;
    // Leave dialogs and menus that live outside the reader alone.
    if (target instanceof Element && target !== document.body && !this.root.contains(target)
      && target.closest("fluent-dialog, [role='dialog'], [role='alertdialog']")) return;

    let command = null;
    switch (e.key) {
      case "j": case "ArrowDown": this.scrollBy(LINE_PX); break;
      case "k": case "ArrowUp": this.scrollBy(-LINE_PX); break;
      case "+": case "=": command = "bigger"; break;
      case "-": case "_": command = "smaller"; break;
      case "t": case "T": command = "outline"; break;
      case "f": case "F": command = "focus"; break;
      case "1": command = "reflow"; break;
      case "2": command = "pages"; break;
      case "Escape":
        this.invoke("OnReaderCommand", "escape");
        this.wake();
        return;
      default:
        return;
    }

    e.preventDefault();
    if (command) this.invoke("OnReaderCommand", command);
    if (command !== "focus") this.wake();
  }

  setFocusMode(on) {
    this.focus = !!on;
    if (!this.focus) {
      clearTimeout(this.idleTimer);
      delete this.root.dataset.idle;
    } else {
      this.wake();
    }
  }

  wake() {
    if (!this.focus) return;
    if (this.root.dataset.idle) delete this.root.dataset.idle;
    clearTimeout(this.idleTimer);
    this.idleTimer = setTimeout(() => {
      if (!this.focus) return;
      const bar = this.root.querySelector(".rd-toolbar");
      const keyboardInBar = bar && this.lastInput === "key" && bar.matches(":focus-within");
      if (bar && (bar.matches(":hover") || keyboardInBar || bar.querySelector("[data-open='true']"))) {
        this.wake();
        return;
      }
      // A mouse click left focus on a toolbar button: hand it to the text so the bar can hide and keys still scroll.
      if (bar && bar.matches(":focus-within")) {
        this.scroller()?.focus({ preventScroll: true, focusVisible: false });
      }
      this.root.dataset.idle = "true";
    }, IDLE_MS);
  }

  dispose() {
    clearTimeout(this.idleTimer);
    document.removeEventListener("keydown", this.onKey);
    this.root.removeEventListener("pointermove", this.onWake);
    this.root.removeEventListener("pointerdown", this.onWake);
    this.root.removeEventListener("touchstart", this.onWake);
    this.root.removeEventListener("focusin", this.onWake);
    this.dotnet = null;
  }
}

export function attach(root, dotnet) {
  return new ReaderPage(root, dotnet);
}
