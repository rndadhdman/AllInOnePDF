// Reflow reading surface: reading progress, scroll-spy (IntersectionObserver) and scroll position memory.

const REPORT_DELAY_MS = 400;
const SPY_LINE_PX = 96;

class Surface {
  constructor(scroller, progress, dotnet) {
    this.scroller = scroller;
    this.progress = progress;
    this.dotnet = dotnet;
    this.frame = 0;
    this.reportTimer = 0;
    this.activeId = null;
    this.headings = [];
    this.observer = null;
    this.reduce = window.matchMedia("(prefers-reduced-motion: reduce)");

    this.onScroll = () => {
      if (this.frame) return;
      this.frame = requestAnimationFrame(() => {
        this.frame = 0;
        this.update();
      });
    };
    scroller.addEventListener("scroll", this.onScroll, { passive: true });
    this.onResize = () => this.onScroll();
    window.addEventListener("resize", this.onResize, { passive: true });

    // Links inside the document never navigate the app: in-page anchors scroll, web and mail links go to the host
    // (system browser / mail app), everything else (relative files, javascript:, data:) is blocked.
    this.onClick = (e) => this.handleLink(e);
    this.onAuxClick = (e) => {
      if (this.linkFrom(e)) e.preventDefault();
    };
    scroller.addEventListener("click", this.onClick);
    scroller.addEventListener("auxclick", this.onAuxClick);
  }

  linkFrom(e) {
    const a = e.target instanceof Element ? e.target.closest("a[href]") : null;
    return a && this.scroller.contains(a) ? a : null;
  }

  handleLink(e) {
    if (e.defaultPrevented) return;
    const a = this.linkFrom(e);
    if (!a) return;
    e.preventDefault();
    const href = (a.getAttribute("href") || "").trim();
    if (href.startsWith("#")) {
      let id = href.slice(1);
      try { id = decodeURIComponent(id); } catch { /* keep raw */ }
      this.scrollToId(id, true);
      return;
    }

    let url;
    try {
      url = new URL(href);
    } catch {
      return; // relative or malformed: nothing to open
    }
    if (url.protocol === "http:" || url.protocol === "https:" || url.protocol === "mailto:") {
      this.invoke("OnExternalLink", url.href);
    }
  }

  // Footnote references get ids and each footnote a back link, so readers can jump there and back.
  linkFootnotes() {
    const seen = new Set();
    for (const ref of this.scroller.querySelectorAll(".aio-doc .aio-fnref a[href^='#fn-']")) {
      const label = ref.getAttribute("href").slice(4);
      const note = this.find("fn-" + label);
      if (!note) continue;
      if (!ref.id && !seen.has(label)) ref.id = "fnref-" + label;
      seen.add(label);
      ref.setAttribute("aria-describedby", note.id);
      if (ref.id && !note.querySelector(".aio-fnback")) {
        const back = document.createElement("a");
        back.className = "aio-fnback";
        back.href = "#" + ref.id;
        back.setAttribute("aria-label", "Back to the reference");
        back.textContent = "↩︎";
        note.append(" ", back);
      }
    }
  }

  find(id) {
    const el = id ? document.getElementById(id) : null;
    return el && this.scroller.contains(el) ? el : null;
  }

  invoke(method, ...args) {
    try {
      this.dotnet?.invokeMethodAsync(method, ...args).catch(() => { });
    } catch {
      // circuit gone
    }
  }

  fraction() {
    const s = this.scroller;
    const max = s.scrollHeight - s.clientHeight;
    return max <= 0 ? 1 : Math.min(1, Math.max(0, s.scrollTop / max));
  }

  update() {
    const f = this.fraction();
    if (this.progress) {
      this.progress.style.transform = `scaleX(${f})`;
      this.progress.parentElement?.setAttribute("aria-valuenow", String(Math.round(f * 100)));
    }

    clearTimeout(this.reportTimer);
    this.reportTimer = setTimeout(() => this.invoke("OnScrollFraction", this.fraction()), REPORT_DELAY_MS);
    this.spy();
  }

  // Active section = last heading whose top is above the reading line; last one when scrolled to the end.
  spy() {
    if (this.headings.length === 0) return;
    const top = this.scroller.getBoundingClientRect().top + SPY_LINE_PX;
    let active = this.headings[0];
    for (const h of this.headings) {
      if (h.getBoundingClientRect().top <= top) active = h;
      else break;
    }
    if (this.fraction() >= 0.998) active = this.headings[this.headings.length - 1];
    const id = active?.id ?? null;
    if (id !== this.activeId) {
      this.activeId = id;
      this.invoke("OnActiveHeading", id);
    }
  }

  // Called after the document HTML changes: re-observe headings and restore the remembered position.
  refresh(restoreFraction) {
    this.observer?.disconnect();
    this.headings = Array.from(this.scroller.querySelectorAll(".aio-doc .aio-h[id]"));
    this.activeId = null;
    this.observer = new IntersectionObserver(() => this.spy(), {
      root: this.scroller,
      rootMargin: `-${SPY_LINE_PX - 8}px 0px -55% 0px`,
      threshold: [0, 1],
    });
    for (const h of this.headings) this.observer.observe(h);
    this.linkFootnotes();

    const restore = () => {
      if (typeof restoreFraction === "number" && restoreFraction > 0) {
        const max = this.scroller.scrollHeight - this.scroller.clientHeight;
        this.scroller.scrollTop = Math.round(restoreFraction * Math.max(0, max));
      } else {
        this.scroller.scrollTop = 0;
      }
      this.update();
    };
    restore();
    // Web fonts change line breaks; restore once more when they are ready.
    if (restoreFraction > 0 && document.fonts?.ready) {
      document.fonts.ready.then(() => { if (this.scroller.isConnected) restore(); });
    }
  }

  // Keeps the reading position when typography changes reflow the text.
  keepPosition(fraction) {
    const max = this.scroller.scrollHeight - this.scroller.clientHeight;
    this.scroller.scrollTop = Math.round(fraction * Math.max(0, max));
    this.update();
  }

  getFraction() {
    return this.fraction();
  }

  // Scrolls to an element of the document (smooth unless reduced motion). With moveFocus, keyboard users land on
  // the target too (footnotes, in-document anchors).
  scrollToId(id, moveFocus) {
    const el = this.find(id);
    if (!el) return false;
    el.scrollIntoView({ behavior: this.reduce.matches ? "auto" : "smooth", block: "start" });
    if (moveFocus) {
      if (!el.hasAttribute("tabindex") && !el.matches("a[href], button, input, select, textarea")) el.setAttribute("tabindex", "-1");
      el.focus({ preventScroll: true });
    }
    return true;
  }

  dispose() {
    cancelAnimationFrame(this.frame);
    clearTimeout(this.reportTimer);
    this.scroller.removeEventListener("click", this.onClick);
    this.scroller.removeEventListener("auxclick", this.onAuxClick);
    this.scroller.removeEventListener("scroll", this.onScroll);
    window.removeEventListener("resize", this.onResize);
    this.observer?.disconnect();
    this.observer = null;
    this.headings = [];
    this.dotnet = null;
  }
}

export function create(scroller, progress, dotnet) {
  return new Surface(scroller, progress, dotnet);
}
