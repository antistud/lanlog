import { LogrrLevel } from "./levels.js";

/** A JSON-serialisable property value. Anything else is coerced to a string on the wire. */
export type PropertyValue =
  | string
  | number
  | boolean
  | null
  | undefined
  | PropertyValue[]
  | { [key: string]: PropertyValue };

/**
 * A single log event to ship to Logrr. The message template and structured properties are
 * preserved as first-class fields (not flattened into a string), matching Logrr's CLEF ingest.
 */
export interface LogrrEvent {
  /** UTC timestamp. Defaults to `new Date()` at emit time when omitted. */
  timestamp?: Date;

  level?: LogrrLevel;

  /** Message template with named holes, e.g. `"Payment {Amount} failed for {UserId}"`. */
  messageTemplate: string;

  /** Optional pre-rendered message. When omitted, Logrr renders the template server-side. */
  renderedMessage?: string;

  /** Exception text — pass an `Error` and its `stack`/`message` is captured. */
  exception?: string | Error;

  traceId?: string;

  spanId?: string;

  /** Logical source; emitted as the `SourceContext` property. */
  source?: string;

  /** Structured properties, preserved as first-class fields. */
  properties?: Record<string, PropertyValue>;
}

/** Normalise an exception input to the string form Logrr stores. */
export function exceptionText(ex: string | Error | undefined): string | undefined {
  if (ex == null) return undefined;
  if (typeof ex === "string") return ex;
  return ex.stack || `${ex.name}: ${ex.message}`;
}
