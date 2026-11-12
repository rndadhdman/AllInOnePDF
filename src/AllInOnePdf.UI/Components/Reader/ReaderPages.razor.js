// Pages mode (print preview): lazy page loading, current page indicator, viewport size for fit-width.

class Pages {
  constructor(scroller, progress, dotnet) {
    this.scroller = scroller;
    this.progress = progress;
    this.dotnet = dotnet;
    this.visible = new Set();
    this.visibleTimer = 0;
    this.resizeTimer = 0;
    this.frame = 0;
    this.current = -1;
    this.lastWidth = 0;
    this.reduce = window.matchMedia("(prefers-reduced-motion: reduce)");

    this.io = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        const index = Number(entry.target.dataset.page);
        if (entry.isIntersecting) this.visible.add(index);
        else this.visible.delete(index);
      }
      clearTimeout(this.visibleTimer);
      this.visibleTimer = setTimeout(() => this.invoke("OnVisiblePages", Array.from(this.visible).sort((a, b) => a - b)), 60);
    }, { root: scroller, rootMargin: "100% 0px 100% 0px", threshold: 0 });

    this.onScroll = () => {
      if (this.frame) return;
      this.frame = requestAnimationFrame(() => {
        this.frame = 0;
        this.update();
      });
    };
    scroller.addEventListener("scroll", this.onScroll, { passive: true });

    this.ro = new ResizeObserver(() => {
      clearTimeout(this.resizeTimer);
      this.resizeTimer = setTimeout(() => this.reportViewport(), 120);
    });
    this.ro.observe(scroller);
    this.reportViewport();
  }

  invoke(method, ...args) {
    try {
      this.dotnet?.invokeMethodAsync(method, ...args).catch(() => { });
    } catch {
      // circuit gone
    }
  }

  reportViewport() {
    const style = getComputedStyle(this.scroller);
    const width = this.scroller.clientWidth - parseFloat(style.paddingLeft || "0") - parseFloat(style.paddingRight || "0");
    if (Math.abs(width - this.lastWidth) < 1) return;
    this.lastWidth = width;
    this.invoke("OnViewport", width, window.devicePixelRatio || 1);
  }

  fraction() {
    const s = this.scroller;
    const max = s.scrollHeight - s.clientHeight;
    return max <= 0 ? 0 : Math.min(1, Math.max(0, s.scrollTop / max));
  }

  update() {
    const f = this.fraction();
    if (this.progress) {
      this.progress.style.transform = `scaleX(${f})`;
      this.progress.parentElement?.setAttribute("aria-valuenow", String(Math.round(f * 100)));
    }

    const box = this.scroller.getBoundingClientRect();
    const line = box.top + box.height * 0.4;
    let current = 0;
    for (const page of this.scroller.querySelectorAll("[data-page]")) {
      const r = page.getBoundingClientRect();
      if (r.top <= line) current = Number(page.dataset.page);
      else break;
    }
    if (current !== this.current) {
      this.current = current;
      this.invoke("OnCurrentPage", current);
    }
  }

  // Re-observes page placeholders after the page list or zoom changed.
  refresh(keepFraction) {
    this.io.disconnect();
    this.visible.clear();
    for (const page of this.scroller.querySelectorAll("[data-page]")) this.io.observe(page);
    if (typeof keepFraction === "number" && keepFraction >= 0) {
      const max = this.scroller.scrollHeight - this.scroller.clientHeight;
      this.scroller.scrollTop = Math.round(keepFraction * Math.max(0, max));
    }
    this.current = -1;
    this.update();
  }

  getFraction() {
    return this.fraction();
  }

  scrollToPage(index) {
    const page = this.scroller.querySelector(`[data-page='${index}']`);
    page?.scrollIntoView({ behavior: this.reduce.matches ? "auto" : "smooth", block: "start" });
  }

  dispose() {
    cancelAnimationFrame(this.frame);
    clearTimeout(this.visibleTimer);
    clearTimeout(this.resizeTimer);
    this.io.disconnect();
    this.ro.disconnect();
    this.scroller.removeEventListener("scroll", this.onScroll);
    this.dotnet = null;
  }
}

export function create(scroller, progress, dotnet) {
  return new Pages(scroller, progress, dotnet);
}
