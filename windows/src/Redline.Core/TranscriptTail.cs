// Reads the part of an append only transcript that has not been read yet, handing back each
// new line with the byte offset it started at, so a record with no id still dedups stably.
using System.Globalization;
using System.Text;

namespace Redline.Core;

public static class TranscriptTail
{
    /// Read this much at a time: no syscall storm on a first run, no 900 MB corpus in memory.
    internal const int ChunkSize = 4 << 20;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// Calls `line` for every complete line at or after `offset`, and returns the offset just
    /// past the last complete line. A partial trailing line is left for the next pass, whole.
    public static long Read(string path, long offset, Action<string, long> line)
    {
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch { return offset; }
        using (fs)
        {
            var start = Math.Max(0, offset);
            try { fs.Seek(start, SeekOrigin.Begin); } catch { return offset; }

            long consumed = start;
            long bufferStart = consumed;
            var buffer = new List<byte>();
            var chunk = new byte[ChunkSize];
            while (true)
            {
                int n;
                try { n = fs.Read(chunk, 0, chunk.Length); } catch { break; }
                if (n <= 0) break;
                buffer.AddRange(new ArraySegment<byte>(chunk, 0, n));
                var arr = buffer.ToArray();
                int searchFrom = 0;
                int newline;
                while ((newline = Array.IndexOf(arr, (byte)0x0A, searchFrom)) >= 0)
                {
                    if (newline > searchFrom && Decode(arr, searchFrom, newline - searchFrom) is { } text)
                        line(text, bufferStart + searchFrom);
                    searchFrom = newline + 1;
                }
                bufferStart += searchFrom;
                consumed = bufferStart;
                buffer = new List<byte>(arr.Length - searchFrom);
                buffer.AddRange(new ArraySegment<byte>(arr, searchFrom, arr.Length - searchFrom));
            }
            return consumed;
        }
    }

    /// Where to start given the last mark. A file smaller than what was read was truncated or
    /// rewritten, and stale offsets would silently skip real usage, so it starts over.
    public static long StartOffset(IngestMark? mark, long size)
    {
        if (mark is null) return 0;
        if (size < mark.ByteOffset) return 0;
        return mark.ByteOffset;
    }

    private static string? Decode(byte[] bytes, int index, int count)
    {
        try { return StrictUtf8.GetString(bytes, index, count); } catch { return null; }
    }

    private static readonly string[] IsoFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz",
    };

    /// ISO 8601 with or without fractional seconds, zone required, as the Swift formatters read it.
    internal static DateTimeOffset? ParseTimestamp(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return DateTimeOffset.TryParseExact(s, IsoFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }
}
