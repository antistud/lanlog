import { LogrrClient } from "./client.js";

/**
 * In a browser, flush the client when the page is being hidden or unloaded, so buffered
 * events aren't lost on navigation or tab close. `pagehide` and `visibilitychange` are the
 * reliable signals (`beforeunload`/`unload` don't fire on mobile). No-op outside a browser.
 *
 * Returns a function that removes the listeners.
 */
export function flushOnPageHide(client: LogrrClient): () => void {
  if (typeof document === "undefined" || typeof addEventListener !== "function") {
    return () => {};
  }
  const onHide = (): void => {
    client.flush().catch(() => {});
  };
  const onVisibility = (): void => {
    if (document.visibilityState === "hidden") onHide();
  };
  addEventListener("pagehide", onHide);
  document.addEventListener("visibilitychange", onVisibility);
  return () => {
    removeEventListener("pagehide", onHide);
    document.removeEventListener("visibilitychange", onVisibility);
  };
}
