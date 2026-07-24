/**
 * Severity, matching the Logrr server's level scale (SPEC §5). The numeric values line up
 * with the server so a level can be compared with `>=` for client-side filtering.
 */
export enum LogrrLevel {
  Verbose = 0,
  Debug = 1,
  Information = 2,
  Warning = 3,
  Error = 4,
  Fatal = 5,
}

const NAMES: Record<LogrrLevel, string> = {
  [LogrrLevel.Verbose]: "Verbose",
  [LogrrLevel.Debug]: "Debug",
  [LogrrLevel.Information]: "Information",
  [LogrrLevel.Warning]: "Warning",
  [LogrrLevel.Error]: "Error",
  [LogrrLevel.Fatal]: "Fatal",
};

/** The `@l` string Logrr expects on the wire. Unknown values fall back to Information. */
export function levelName(level: LogrrLevel): string {
  return NAMES[level] ?? "Information";
}
