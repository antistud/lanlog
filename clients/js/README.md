# @logrr/client

Ship structured logs to a [Logrr](https://github.com/antistud/lanlog) server from Node.js
and the browser. Zero dependencies; uses the platform `fetch`. Works with ESM and CommonJS,
and ships TypeScript types.

## Install

```bash
npm install @logrr/client
```

## Use it

```ts
import { LogrrClient, LogrrLevel } from "@logrr/client";

const logrr = new LogrrClient({
  endpoint: "https://logrr.internal",
  apiKey: "lg_myapp_xxxxxxxxxxxxxxxxxxxxxx",
});

// Message templates + structured properties are preserved end to end — not flattened.
logrr.info("User {UserId} signed in from {Ip}", { UserId: 1042, Ip: "10.0.0.7" });
logrr.error("Payment {Amount} failed for {UserId}", err, { Amount: 49.99, UserId: 1042 });
```

`logrr.error(template, error?, properties?)` and `logrr.fatal(...)` take the error second;
`verbose` / `debug` / `info` / `warn` take `(template, properties?)`. For full control use
`log(level, template, properties?, error?)` or `emit(event)`.

## Options

```ts
new LogrrClient({
  endpoint: "https://logrr.internal",
  apiKey: "lg_myapp_...",
  minimumLevel: LogrrLevel.Information, // drop below this client-side (default: Verbose)
  batchSizeLimit: 500,                  // flush when this many are buffered
  flushIntervalMs: 2000,                // …or after this long, whichever comes first
  queueLimit: 100_000,                  // hard cap; beyond it events are dropped, never OOM
  requestTimeoutMs: 10_000,
  fetch: myFetch,                       // custom fetch (proxy/agent); defaults to global fetch
  onError: (err, dropped) => {},        // notified when a batch is shed
});
```

## Behaviour

- **Batching, off the calling path.** `emit`/`log` return immediately; events flush on a size
  (500) or time (2s) trigger.
- **Fire-and-forget.** A Logrr outage delays and (past `queueLimit`) drops events; it never
  blocks or throws into your app. `client.droppedCount` and `onError` expose loss.
- **Seq-compatible wire format.** Posts newline-delimited CLEF to `/api/events/raw` with the
  `X-Logrr-ApiKey` header — the same format Serilog's Seq sink emits.

Call `await client.close()` on graceful shutdown for a best-effort final flush:

```ts
process.on("SIGTERM", () => client.close());
```

## In the browser

Flush buffered events when the tab is hidden or closed so they aren't lost on navigation:

```ts
import { LogrrClient, flushOnPageHide } from "@logrr/client";

const logrr = new LogrrClient({ endpoint, apiKey });
flushOnPageHide(logrr); // no-op outside a browser
```

> Browser note: `apiKey` is visible to anyone with the page. Only ship an **ingest-scoped**
> token from front-end code, and prefer proxying through your own backend if you can.

## Build from source

```bash
npm install
npm run build   # → dist/ (ESM + CJS + .d.ts)
npm test
```
