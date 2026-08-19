// Reads the part of an append only transcript that has not been read yet.
//
// Every provider this app understands writes JSONL and only ever appends to it. Re-reading
// all of it on a five minute timer is how a menu bar utility ends up parsing the better part
// of a gigabyte to discover that nothing happened. This hands back only the new lines, with
// the byte offset each one started at, so a record with no id of its own still has something
// stable to be deduped on.
import Foundation

public enum TranscriptTail {
    /// Read this much at a time. Large enough that a first run is not a syscall storm, small
    /// enough that a 900 MB corpus never lands in memory at once.
    static let chunkSize = 4 << 20

    /// Calls `line` for every complete line at or after `offset`, and returns the offset
    /// just past the last complete line.
    ///
    /// A partial trailing line is deliberately left unconsumed: a transcript being written
    /// right now ends mid record, and parsing half a JSON object once is a bug that hides
    /// until the day it matters. Next pass starts at the newline before it and gets it whole.
    @discardableResult
    public static func read(url: URL, from offset: Int,
                            line: (String, Int) -> Void) -> Int {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return offset }
        defer { try? handle.close() }
        guard (try? handle.seek(toOffset: UInt64(max(0, offset)))) != nil else { return offset }

        var consumed = max(0, offset)
        var buffer = Data()
        var bufferStart = consumed

        while true {
            guard let chunk = try? handle.read(upToCount: chunkSize), !chunk.isEmpty else {
                break
            }
            buffer.append(chunk)
            var searchFrom = buffer.startIndex
            while let newline = buffer[searchFrom...].firstIndex(of: 0x0A) {
                let raw = buffer[searchFrom..<newline]
                if !raw.isEmpty, let text = String(data: raw, encoding: .utf8) {
                    line(text, bufferStart + (searchFrom - buffer.startIndex))
                }
                searchFrom = buffer.index(after: newline)
            }
            let leftover = searchFrom - buffer.startIndex
            bufferStart += leftover
            consumed = bufferStart
            buffer = Data(buffer[searchFrom...])
        }
        return consumed
    }

    /// Where to start reading a file given what was recorded about it last time.
    ///
    /// Truncation and rewrites both look like "the file got smaller than we already read",
    /// and the only safe answer is to start over: the offsets we hold no longer point at the
    /// records we think they do, and stale offsets would silently skip real usage.
    public static func startOffset(mark: IngestMark?, size: Int) -> Int {
        guard let mark else { return 0 }
        if size < mark.byteOffset { return 0 }
        return mark.byteOffset
    }
}
