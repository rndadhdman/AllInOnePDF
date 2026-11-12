// Nav helpers: auto-collapse below 900 px and arrow-key movement between links.
const NARROW_QUERY = "(max-width: 900px)";

export function init(nav, dotnet) {
  if (!nav) return false;
  dispose(nav);

  const mq = window.matchMedia(NARROW_QUERY);
  const onMedia = () => dotnet.invokeMethodAsync("OnNarrowChanged", mq.matches).catch(() => { });

  const onKey = (e) => {
    if (e.key === "Escape") {
      dotnet.invokeMethodAsync("OnEscape").catch(() => { });
      return;
    }
    const links = Array.from(nav.querySelectorAll("a.aio-nav-link"));
    const index = links.indexOf(document.activeElement);
    if (index < 0) return;
    let next = -1;
    switch (e.key) {
      case "ArrowDown": next = (index + 1) % links.length; break;
      case "ArrowUp": next = (index - 1 + links.length) % links.length; break;
      case "Home": next = 0; break;
      case "End": next = links.length - 1; break;
      default: return;
    }
    e.preventDefault();
    links[next].focus();
  };

  mq.addEventListener("change", onMedia);
  nav.addEventListener("keydown", onKey);
  nav._aioDispose = () => {
    mq.removeEventListener("change", onMedia);
    nav.removeEventListener("keydown", onKey);
  };
  return mq.matches;
}

export function dispose(nav) {
  if (nav && nav._aioDispose) {
    nav._aioDispose();
    delete nav._aioDispose;
  }
}
