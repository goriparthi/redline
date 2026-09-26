// Reads Claude Code's live session registry (~/.claude/sessions/<PID>.json), one file per
// running session, so RedLine can say which sessions are working and which are blocked.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Redline.Core;

/// Where a session is, as far as its record admits. Closed only for ordering; the raw string
/// survives on the session so an upstream addition still displays.
public enum FleetState
{
    /// Blocked on the user. The one state this whole feature exists to surface.
    Waiting = 0,
    Busy = 1,
    Idle = 2,
    Unknown = 3,
}

public static class FleetStateExt
{
    internal static FleetState Of(string? raw) => raw switch
    {
        "waiting" => FleetState.Waiting,
        "busy" => FleetState.Busy,
        "idle" => FleetState.Idle,
        _ => FleetState.Unknown,
    };
}

/// One live Claude Code session. Only `Pid` and `Cwd` are required; the rest is undocumented
/// internals, so a missing or renamed field degrades one row rather than dropping the session.
public sealed record FleetSession(int Pid, string Cwd)
{
    public string? SessionId { get; set; }
    public string? Name { get; set; }
    /// Verbatim from the record. `State` is the interpreted form; this is what it said.
    public string? Status { get; set; }
    /// Present only while waiting; observed as "input needed"
    public string? WaitingFor { get; set; }
    public DateTimeOffset? StatusUpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? Version { get; set; }
    /// Open ended by design: observed "cli", and an enum would drop sessions rather than label them.
    public string? Entrypoint { get; set; }
    public string? Kind { get; set; }
    public string? BridgeSessionId { get; set; }
    /// The file this was read from, so a caller can watch it: a status change rewrites it in place.
    public string? RecordPath { get; set; }

    public FleetState State => FleetStateExt.Of(Status);

    /// What the row calls the session: its own name when it has one, else the folder.
    public string Label => !string.IsNullOrEmpty(Name) ? Name : Folder;

    public string Folder
    {
        get
        {
            var trimmed = Cwd.TrimEnd('/', '\\');
            if (trimmed.Length == 0) return Cwd;
            var name = Path.GetFileName(trimmed);
            return name.Length == 0 ? trimmed : name;
        }
    }

    /// How long it has been in the current status: for a waiting session, how long unattended.
    public double? TimeInStatus(DateTimeOffset? now = null)
    {
        if (StatusUpdatedAt is not { } at) return null;
        return Math.Max(0, ((now ?? DateTimeOffset.UtcNow) - at).TotalSeconds);
    }

    /// The one free bridge between the local session and its cloud view.
    public Uri? ClaudeUrl =>
        !string.IsNullOrEmpty(BridgeSessionId) &&
        Uri.TryCreate($"https://claude.ai/code/{BridgeSessionId}", UriKind.Absolute, out var u) ? u : null;
}

/// `Sessions` is sorted waiting first, then busy, then idle, each by longest in that status.
public sealed record FleetSnapshot(IReadOnlyList<FleetSession> Sessions)
{
    public FleetSnapshot() : this(Array.Empty<FleetSession>()) { }

    public bool IsEmpty => Sessions.Count == 0;
    public List<FleetSession> Waiting => Sessions.Where(s => s.State == FleetState.Waiting).ToList();
    public List<FleetSession> Busy => Sessions.Where(s => s.State == FleetState.Busy).ToList();

    public bool Equals(FleetSnapshot? o) => o is not null && Sessions.SequenceEqual(o.Sessions);
    public override int GetHashCode() => Sessions.Count;
}

/// Whether a PID is alive, and when it started. Injected so the tests can present a dead
/// process without killing anything.
public sealed class ProcessProbe
{
    public Func<int, DateTimeOffset?> StartTime { get; }

    public ProcessProbe(Func<int, DateTimeOffset?> startTime) { StartTime = startTime; }

    public static readonly ProcessProbe Live = new(ProcessStartTime);

    /// Start time from the process table. Absent means the process is gone (or is not ours to
    /// inspect), which is also the liveness answer, so one call covers both questions.
    public static DateTimeOffset? ProcessStartTime(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return null;
            return new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch { return null; }
    }

    /// Windows has no controlling terminal device to join a session to its tab, so this is
    /// always null; it exists so callers written against the macOS shape still compile.
    public static string? TtyPath(int pid) => null;
}

/// Scans the live session registry. Local only by design: cloud sessions and other machines have
/// no public API, and Codex has no live registry at all, so the two are not unified.
public sealed class ClaudeFleetStore
{
    private readonly string _root;
    private readonly ProcessProbe _probe;
    /// ctime format, as Claude Code writes it. Observed in UTC but naming no zone, so both
    /// readings are kept and either may match; see IsLive.
    private const string CtimeFormat = "ddd MMM d HH:mm:ss yyyy";

    /// One record per running session, deleted on exit. Watched, never written to.
    public static string DefaultRoot => RedlineHome.PathFor(".claude/sessions");

    public ClaudeFleetStore(string? root = null, ProcessProbe? probe = null)
    {
        _root = root ?? DefaultRoot;
        _probe = probe ?? ProcessProbe.Live;
    }

    public FleetSnapshot Scan(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        List<string> names;
        // No directory is the normal state on a PC without Claude Code, not an error
        try { names = Directory.EnumerateFileSystemEntries(_root).Select(p => Path.GetFileName(p)).ToList(); }
        catch { return new FleetSnapshot(); }
        var output = new List<FleetSession>();
        foreach (var name in names)
        {
            // Siblings named <PID>.<hash>.key hold secrets. Only the plain record is read.
            if (!name.EndsWith(".json", StringComparison.Ordinal) || name.Contains(".key")) continue;
            var path = Path.Combine(_root, name);
            byte[] data;
            try { data = File.ReadAllBytes(path); } catch { continue; }
            if (Decode(data) is not { } record) continue;
            if (!IsLive(record)) continue;
            record.Session.RecordPath = path;
            output.Add(record.Session);
        }
        return new FleetSnapshot(Sorted(output, t));
    }

    /// A decoded record plus the start time it claims: not shown, but it proves the record is
    /// not a leftover. The claimed start is read both ways, because the field states no zone.
    internal sealed record Record(FleetSession Session, List<DateTimeOffset> ProcStart);

    /// Decodes one record by hand so an unknown field is ignored and a missing one costs a
    /// property instead of the whole session.
    internal Record? Decode(byte[] data)
    {
        JsonObject? obj;
        try { obj = System.Text.Json.Nodes.JsonNode.Parse(data) as JsonObject; } catch { return null; }
        if (obj is null || Int32(obj["pid"]) is not { } pid || pid <= 0 ||
            Json.Str(obj["cwd"]) is not { Length: > 0 } cwd) return null;
        var s = new FleetSession(pid, cwd)
        {
            SessionId = Json.Str(obj["sessionId"]),
            Name = Json.Str(obj["name"]),
            Status = Json.Str(obj["status"]),
            WaitingFor = Json.Str(obj["waitingFor"]),
            Version = Json.Str(obj["version"]),
            Entrypoint = Json.Str(obj["entrypoint"]),
            Kind = Json.Str(obj["kind"]),
            BridgeSessionId = Json.Str(obj["bridgeSessionId"]),
            StatusUpdatedAt = EpochMillis(obj["statusUpdatedAt"]) ?? EpochMillis(obj["updatedAt"]),
            StartedAt = EpochMillis(obj["startedAt"]),
        };
        // ctime space pads a single digit day, which the exact format will not match
        var raw = Json.Str(obj["procStart"])?.Replace("  ", " ");
        var claimed = new List<DateTimeOffset>();
        if (raw is not null)
        {
            foreach (var style in new[] { DateTimeStyles.AssumeUniversal, DateTimeStyles.AssumeLocal })
            {
                if (DateTimeOffset.TryParseExact(raw, CtimeFormat, CultureInfo.InvariantCulture,
                        style, out var d)) claimed.Add(d);
            }
        }
        return new Record(s, claimed);
    }

    /// Records outlive killed sessions and PIDs are reused, so the claimed start is checked against
    /// the process table's, either zone reading counting. Nothing here deletes from ~/.claude.
    private bool IsLive(Record r)
    {
        if (_probe.StartTime(r.Session.Pid) is not { } actual) return false;
        if (r.ProcStart.Count == 0) return true;
        return r.ProcStart.Any(p => Math.Abs((actual - p).TotalSeconds) < 2);
    }

    /// Waiting first, because only that state needs a person. Within a state the oldest floats
    /// up, so the session stuck longest is on top.
    internal List<FleetSession> Sorted(IEnumerable<FleetSession> sessions, DateTimeOffset now) =>
        sessions.OrderBy(s => s.State)
            .ThenBy(s => s.StatusUpdatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(s => s.Pid).ToList();

    private static int? Int32(JsonNode? v)
    {
        if (Json.Long(v) is { } l) return unchecked((int)l);
        if (Json.Num(v) is { } d) return (int)d;
        return null;
    }

    private static DateTimeOffset? EpochMillis(JsonNode? v)
    {
        if (Json.Num(v) is not { } ms || ms <= 0) return null;
        try { return DateTimeOffset.UnixEpoch.AddMilliseconds(ms); } catch { return null; }
    }
}
