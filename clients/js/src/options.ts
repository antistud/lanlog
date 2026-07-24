import { LogrrLevel } from "./levels.js";

/** Configuration for {@link LogrrClient}. */
export interface LogrrClientOptions {
  /** Base URL of the Logrr server, e.g. `https://logrr.internal`. */
  endpoint: string;

  /** An ingest-scoped token: `lg_{appId}_{secret}`. */
  apiKey: string;

  /** Events below this level are dropped client-side. Default: {@link LogrrLevel.Verbose}. */
  minimumLevel?: LogrrLevel;

  /** Flush when this many events are buffered. Default: 500. */
  batchSizeLimit?: number;

  /** …or after this many milliseconds, whichever comes first. Default: 2000. */
  flushIntervalMs?: number;

  /**
   * Hard cap on the in-memory buffer. Beyond it, new events are dropped rather than growing
   * unbounded — logging must never take down the host app. Default: 100000.
   */
  queueLimit?: number;

  /** Per-request timeout in milliseconds. Default: 10000. */
  requestTimeoutMs?: number;

  /**
   * `fetch` implementation to use. Defaults to the global `fetch` (Node 18+, browsers,
   * Deno, Bun). Pass one explicitly to route through a proxy or a custom agent.
   */
  fetch?: typeof fetch;

  /**
   * Invoked when a batch is dropped (buffer full, send failure, or non-2xx response). The
   * client itself never throws into your app; this is the only signal of loss besides
   * {@link LogrrClient.droppedCount}.
   */
  onError?: (error: unknown, droppedCount: number) => void;
}

/** Options with defaults applied. */
export interface ResolvedOptions extends Required<Omit<LogrrClientOptions, "onError">> {
  onError?: (error: unknown, droppedCount: number) => void;
  ingestUrl: string;
}

export function resolveOptions(options: LogrrClientOptions): ResolvedOptions {
  if (!options.endpoint || !options.endpoint.trim()) {
    throw new Error("Logrr endpoint is required.");
  }
  if (!options.apiKey || !options.apiKey.trim()) {
    throw new Error("Logrr API key is required.");
  }
  const fetchImpl = options.fetch ?? globalThis.fetch;
  if (typeof fetchImpl !== "function") {
    throw new Error(
      "No fetch implementation available. Pass options.fetch or run on Node 18+ / a browser.",
    );
  }
  return {
    endpoint: options.endpoint,
    apiKey: options.apiKey,
    minimumLevel: options.minimumLevel ?? LogrrLevel.Verbose,
    batchSizeLimit: options.batchSizeLimit ?? 500,
    flushIntervalMs: options.flushIntervalMs ?? 2000,
    queueLimit: options.queueLimit ?? 100_000,
    requestTimeoutMs: options.requestTimeoutMs ?? 10_000,
    fetch: fetchImpl,
    onError: options.onError,
    ingestUrl: options.endpoint.replace(/\/+$/, "") + "/api/events/raw",
  };
}
