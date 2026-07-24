import { serializeClef } from "./clef.js";
import { LogrrEvent, PropertyValue } from "./event.js";
import { LogrrLevel } from "./levels.js";
import { LogrrClientOptions, ResolvedOptions, resolveOptions } from "./options.js";

const CONTENT_TYPE = "application/vnd.serilog.clef";

/**
 * A batching, fire-and-forget ingest client for Node.js and the browser. Events are buffered
 * and flushed on a size or time trigger, off the calling path. A server outage delays and
 * (past the buffer cap) drops events — it never blocks or throws into the host app.
 */
export class LogrrClient {
  private readonly opts: ResolvedOptions;
  private queue: LogrrEvent[] = [];
  private dropped = 0;
  private flushChain: Promise<void> = Promise.resolve();
  private timer: ReturnType<typeof setInterval> | null = null;
  private closed = false;

  constructor(options: LogrrClientOptions) {
    this.opts = resolveOptions(options);
    this.timer = setInterval(() => this.fireAndForgetFlush(), this.opts.flushIntervalMs);
    // Don't keep a Node process alive just for the flush timer.
    (this.timer as unknown as { unref?: () => void }).unref?.();
  }

  /** Events dropped so far because the buffer was full or a send failed. */
  get droppedCount(): number {
    return this.dropped;
  }

  /** Buffer and (eventually) ship an event. Returns immediately. */
  emit(event: LogrrEvent): void {
    if (this.closed) return;
    const level = event.level ?? LogrrLevel.Information;
    if (level < this.opts.minimumLevel) return;

    if (this.queue.length >= this.opts.queueLimit) {
      this.dropped++;
      return;
    }

    this.queue.push(event);
    if (this.queue.length >= this.opts.batchSizeLimit) {
      this.fireAndForgetFlush();
    }
  }

  /** Convenience: emit with a level, template, optional error, and properties. */
  log(
    level: LogrrLevel,
    messageTemplate: string,
    properties?: Record<string, PropertyValue>,
    exception?: string | Error,
  ): void {
    this.emit({ level, messageTemplate, properties, exception });
  }

  verbose(messageTemplate: string, properties?: Record<string, PropertyValue>): void {
    this.log(LogrrLevel.Verbose, messageTemplate, properties);
  }
  debug(messageTemplate: string, properties?: Record<string, PropertyValue>): void {
    this.log(LogrrLevel.Debug, messageTemplate, properties);
  }
  info(messageTemplate: string, properties?: Record<string, PropertyValue>): void {
    this.log(LogrrLevel.Information, messageTemplate, properties);
  }
  warn(messageTemplate: string, properties?: Record<string, PropertyValue>): void {
    this.log(LogrrLevel.Warning, messageTemplate, properties);
  }
  error(
    messageTemplate: string,
    exception?: string | Error,
    properties?: Record<string, PropertyValue>,
  ): void {
    this.log(LogrrLevel.Error, messageTemplate, properties, exception);
  }
  fatal(
    messageTemplate: string,
    exception?: string | Error,
    properties?: Record<string, PropertyValue>,
  ): void {
    this.log(LogrrLevel.Fatal, messageTemplate, properties, exception);
  }

  private fireAndForgetFlush(): void {
    // Deliberately not awaited: the calling path must never block on the network.
    // flush() already accounts for failures via droppedCount/onError.
    this.flush().catch(() => {});
  }

  /**
   * Send all buffered events now. Safe to call concurrently: drains are serialised, and each
   * call resolves after a drain that began after the call — so events queued before it are sent.
   */
  flush(): Promise<void> {
    this.flushChain = this.flushChain.then(
      () => this.drainLoop(),
      () => this.drainLoop(),
    );
    return this.flushChain;
  }

  private async drainLoop(): Promise<void> {
    while (true) {
      const batch = this.queue.splice(0, this.opts.batchSizeLimit);
      if (batch.length === 0) return;
      await this.send(batch);
    }
  }

  private async send(batch: LogrrEvent[]): Promise<void> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), this.opts.requestTimeoutMs);
    try {
      const response = await this.opts.fetch(this.opts.ingestUrl, {
        method: "POST",
        headers: {
          "Content-Type": CONTENT_TYPE,
          "X-Logrr-ApiKey": this.opts.apiKey,
        },
        body: serializeClef(batch),
        signal: controller.signal,
      });
      if (!response.ok) {
        // A rejected batch is dropped — retrying a bad payload forever helps no one.
        this.drop(batch.length, new Error(`Logrr ingest returned HTTP ${response.status}`));
      }
    } catch (err) {
      // Network/transport failure or timeout: drop this batch. Logging is fire-and-forget.
      this.drop(batch.length, err);
    } finally {
      clearTimeout(timeout);
    }
  }

  private drop(count: number, error: unknown): void {
    this.dropped += count;
    try {
      this.opts.onError?.(error, count);
    } catch {
      /* an onError that throws must not escalate */
    }
  }

  /**
   * Best-effort final flush, then stop the timer. After this the client ignores new events.
   * Call on graceful shutdown (e.g. a SIGTERM handler or the host's dispose).
   */
  async close(): Promise<void> {
    if (this.closed) return;
    this.closed = true;
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
    await this.flush();
  }
}
