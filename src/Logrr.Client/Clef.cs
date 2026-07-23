using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Logrr.Client
{
    /// <summary>
    /// Writes events as newline-delimited CLEF — the exact wire format Logrr's
    /// <c>/api/events/raw</c> endpoint accepts (the same format Serilog's Seq sink emits).
    /// </summary>
    internal static class Clef
    {
        public static byte[] Serialize(IReadOnlyList<LogrrEvent> events)
        {
            using (var stream = new MemoryStream())
            {
                for (var i = 0; i < events.Count; i++)
                {
                    if (i > 0)
                    {
                        stream.WriteByte((byte)'\n');
                    }
                    WriteLine(stream, events[i]);
                }
                return stream.ToArray();
            }
        }

        private static void WriteLine(Stream stream, LogrrEvent e)
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("@t", e.Timestamp.ToUniversalTime().ToString("o"));

                if (!string.IsNullOrEmpty(e.MessageTemplate))
                {
                    writer.WriteString("@mt", e.MessageTemplate);
                }
                if (!string.IsNullOrEmpty(e.RenderedMessage))
                {
                    writer.WriteString("@m", e.RenderedMessage);
                }
                writer.WriteString("@l", LevelNames.ToName(e.Level));
                if (!string.IsNullOrEmpty(e.Exception))
                {
                    writer.WriteString("@x", e.Exception);
                }
                if (!string.IsNullOrEmpty(e.TraceId))
                {
                    writer.WriteString("@tr", e.TraceId);
                }
                if (!string.IsNullOrEmpty(e.SpanId))
                {
                    writer.WriteString("@sp", e.SpanId);
                }
                if (!string.IsNullOrEmpty(e.Source))
                {
                    writer.WriteString("SourceContext", e.Source);
                }

                if (e.Properties != null)
                {
                    foreach (var kv in e.Properties)
                    {
                        // Never let a property collide with a reserved field or SourceContext.
                        if (IsReserved(kv.Key))
                        {
                            continue;
                        }
                        writer.WritePropertyName(EscapeReserved(kv.Key));
                        WriteValue(writer, kv.Value);
                    }
                }

                writer.WriteEndObject();
                writer.Flush();
            }
        }

        private static bool IsReserved(string name) =>
            name == "@t" || name == "@m" || name == "@mt" || name == "@l" ||
            name == "@x" || name == "@tr" || name == "@sp" || name == "SourceContext";

        // A literal "@x" property is escaped as "@@x" on the wire (Logrr unescapes it).
        private static string EscapeReserved(string name) =>
            name.Length > 0 && name[0] == '@' ? "@" + name : name;

        private static void WriteValue(Utf8JsonWriter writer, object? value)
        {
            switch (value)
            {
                case null: writer.WriteNullValue(); break;
                case string s: writer.WriteStringValue(s); break;
                case bool b: writer.WriteBooleanValue(b); break;
                case int i: writer.WriteNumberValue(i); break;
                case long l: writer.WriteNumberValue(l); break;
                case double d: writer.WriteNumberValue(d); break;
                case float f: writer.WriteNumberValue(f); break;
                case decimal m: writer.WriteNumberValue(m); break;
                default: writer.WriteStringValue(value.ToString()); break;
            }
        }
    }
}
