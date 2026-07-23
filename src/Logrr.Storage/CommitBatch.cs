using Logrr.Core;

namespace Logrr.Storage;

/// <summary>
/// A committed batch handed to post-commit observers (realtime fan-out, rule evaluation).
/// Delivered after the SQLite transaction commits so a slow subscriber or webhook can
/// never stall ingest (SPEC §4.5).
/// </summary>
public sealed record CommitBatch(string AppId, DateOnly Day, IReadOnlyList<(long Rowid, LogEvent Event)> Rows);
