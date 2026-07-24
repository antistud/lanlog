import { LogrrEvent, exceptionText } from "./event.js";
import { LogrrLevel, levelName } from "./levels.js";

// Reserved CLEF fields (SPEC §6.1). A user property matching one of these is either skipped
// (SourceContext) or escaped (a leading `@` is doubled, which Logrr unescapes).
const RESERVED = new Set(["@t", "@m", "@mt", "@l", "@x", "@tr", "@sp", "SourceContext"]);

/**
 * Serialise events as newline-delimited CLEF — the exact wire format Logrr's
 * `/api/events/raw` endpoint accepts (the same format Serilog's Seq sink emits).
 */
export function serializeClef(events: readonly LogrrEvent[]): string {
  const lines: string[] = [];
  for (const e of events) {
    lines.push(serializeLine(e));
  }
  return lines.join("\n");
}

function serializeLine(e: LogrrEvent): string {
  const obj: Record<string, unknown> = {
    "@t": (e.timestamp ?? new Date()).toISOString(),
  };

  if (e.messageTemplate) obj["@mt"] = e.messageTemplate;
  if (e.renderedMessage) obj["@m"] = e.renderedMessage;
  obj["@l"] = levelName(e.level ?? LogrrLevel.Information);

  const ex = exceptionText(e.exception);
  if (ex) obj["@x"] = ex;
  if (e.traceId) obj["@tr"] = e.traceId;
  if (e.spanId) obj["@sp"] = e.spanId;
  if (e.source) obj["SourceContext"] = e.source;

  if (e.properties) {
    for (const [key, value] of Object.entries(e.properties)) {
      if (value === undefined) continue;
      // Never let a property collide with a reserved field or SourceContext.
      if (RESERVED.has(key)) continue;
      // A literal "@x" property is escaped as "@@x" on the wire (Logrr unescapes it).
      obj[key.startsWith("@") ? "@" + key : key] = value;
    }
  }

  return JSON.stringify(obj);
}
