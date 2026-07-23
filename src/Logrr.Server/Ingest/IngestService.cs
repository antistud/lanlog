using System.Text.Json;
using Logrr.Contracts;
using Logrr.Core;
using Logrr.Storage;
using Logrr.Storage.Control;

namespace Logrr.Server.Ingest;

public sealed class IngestLimits
{
    public int MaxEventBytes { get; set; } = 262_144;
    public int MaxPropertiesPerEvent { get; set; } = 100;
}

/// <summary>
/// Parses ingest payloads into events and enqueues them (SPEC §6). Never fails a whole
/// batch over one bad line — partial failures are reported. Applies the app's minimum-level
/// floor, timestamp-skew bounds, per-event truncation, and the late-arrival marker.
/// </summary>
public sealed class IngestService(IngestPipeline pipeline, IngestLimits limits, Func<DateTimeOffset> clock)
{
    private static readonly TimeSpan MaxPast = TimeSpan.FromDays(30);
    private static readonly TimeSpan MaxFuture = TimeSpan.FromHours(1);

    public IngestResult IngestClef(string body, AppRecord app)
    {
        var now = clock();
        var accepted = 0;
        var rejected = 0;
        var errors = new List<string>();

        var lineNo = 0;
        foreach (var rawLine in body.Split('\n'))
        {
            lineNo++;
            var line = rawLine.Trim('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonElement element;
            try
            {
                using var doc = JsonDocument.Parse(line);
                element = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                rejected++;
                errors.Add($"line {lineNo}: invalid JSON ({ex.Message})");
                continue;
            }

            var result = ClefParser.Parse(element, now);
            if (!result.Ok)
            {
                rejected++;
                errors.Add($"line {lineNo}: {result.Error}");
                continue;
            }

            Handle(result.Event!, app, now, lineNo, line.Length, ref accepted, ref rejected, errors);
        }

        return new IngestResult { Accepted = accepted, Rejected = rejected, Errors = errors };
    }

    public IngestResult IngestPlain(JsonElement root, AppRecord app)
    {
        var now = clock();
        var accepted = 0;
        var rejected = 0;
        var errors = new List<string>();

        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : [root];

        var i = 0;
        foreach (var item in items)
        {
            i++;
            PlainEventDto? dto;
            try
            {
                dto = item.Deserialize<PlainEventDto>();
            }
            catch (JsonException ex)
            {
                rejected++;
                errors.Add($"item {i}: {ex.Message}");
                continue;
            }
            if (dto is null)
            {
                rejected++;
                errors.Add($"item {i}: empty");
                continue;
            }

            var props = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (dto.Properties is { ValueKind: JsonValueKind.Object } p)
            {
                foreach (var prop in p.EnumerateObject())
                {
                    props[prop.Name] = PropertyValue.FromJson(prop.Value);
                }
            }

            var level = LevelMap.ParseOrDefault(dto.Level);
            var message = dto.Message ?? (dto.Template is not null ? MessageTemplate.Render(dto.Template, props) : "");
            var ev = new LogEvent
            {
                Timestamp = dto.Timestamp ?? now,
                Level = level,
                Template = dto.Template,
                Message = message,
                Exception = dto.Exception,
                EventType = EventTypeHash.Compute(dto.Template, message),
                TraceId = dto.TraceId,
                SpanId = dto.SpanId,
                Source = dto.Source,
                Properties = props,
            };

            Handle(ev, app, now, i, message.Length, ref accepted, ref rejected, errors);
        }

        return new IngestResult { Accepted = accepted, Rejected = rejected, Errors = errors };
    }

    private void Handle(LogEvent ev, AppRecord app, DateTimeOffset now, int index, int approxSize,
        ref int accepted, ref int rejected, List<string> errors)
    {
        // Timestamp skew bounds (SPEC §6.3).
        if (ev.Timestamp < now - MaxPast || ev.Timestamp > now + MaxFuture)
        {
            rejected++;
            errors.Add($"line {index}: timestamp out of range");
            return;
        }

        // Server-side minimum-level floor — below is discarded at ingest (SPEC §5.1).
        if (ev.Level < app.MinimumLevel)
        {
            accepted++; // valid, just filtered
            return;
        }

        if (ev.Properties.Count > limits.MaxPropertiesPerEvent)
        {
            rejected++;
            errors.Add($"line {index}: too many properties ({ev.Properties.Count})");
            return;
        }

        ev = ApplyMarkers(ev, approxSize);

        if (pipeline.TryEnqueue(app.Id, ev))
        {
            accepted++;
        }
        else
        {
            rejected++;
            errors.Add($"line {index}: ingest buffer full");
        }
    }

    /// <summary>Add <c>_truncated</c> / <c>_lateArrival</c> markers where applicable (SPEC §4.5, §6.3).</summary>
    private LogEvent ApplyMarkers(LogEvent ev, int approxSize)
    {
        var truncated = approxSize > limits.MaxEventBytes;
        var late = StoragePaths.DayOf(ev.Timestamp) < StoragePaths.DayOf(clock());
        if (!truncated && !late)
        {
            return ev;
        }

        var props = new Dictionary<string, object?>(ev.Properties, StringComparer.Ordinal);
        var message = ev.Message;
        if (truncated)
        {
            props["_truncated"] = true;
            if (message.Length > limits.MaxEventBytes)
            {
                message = message[..limits.MaxEventBytes];
            }
        }
        if (late)
        {
            props["_lateArrival"] = true;
        }

        return new LogEvent
        {
            Timestamp = ev.Timestamp,
            Level = ev.Level,
            Template = ev.Template,
            Message = message,
            Exception = ev.Exception,
            EventType = ev.EventType,
            TraceId = ev.TraceId,
            SpanId = ev.SpanId,
            Source = ev.Source,
            Machine = ev.Machine,
            Properties = props,
        };
    }
}
