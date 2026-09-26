// Exercises the Claude Code session registry reader against synthetic records in a temp dir.
using System.Globalization;
using System.Text.Json.Nodes;
using Redline.Core;

namespace Redline.Core.Tests;

public sealed class ClaudeFleetStoreTests : IDisposable
{
    private readonly string _dir;
    /// A fixed start time every fixture claims, so a record and the probe agree by default
    private static readonly DateTimeOffset Started = DateTimeOffset.FromUnixTimeSeconds(1_787_067_508);
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_787_070_000);

    public ClaudeFleetStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "redline-fleet-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Ctime(DateTimeOffset d, bool utc = false)
    {
        var t = utc ? d.ToUniversalTime() : TimeZoneInfo.ConvertTime(d, TimeZoneInfo.Local);
        return t.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture);
    }

    private void Write(JsonObject obj, int pid) =>
        File.WriteAllText(Path.Combine(_dir, $"{pid}.json"), obj.ToJsonString());

    private static JsonObject Record(int pid, string status, DateTimeOffset statusAt, JsonObject? extra = null)
    {
        var obj = new JsonObject
        {
            ["pid"] = pid,
            ["sessionId"] = $"s-{pid}",
            ["cwd"] = $"/Users/x/work/proj-{pid}",
            ["startedAt"] = Started.ToUnixTimeMilliseconds(),
            ["procStart"] = Ctime(Started),
            ["version"] = "2.1.234",
            ["kind"] = "interactive",
            ["entrypoint"] = "cli",
            ["name"] = $"proj-{pid}",
            ["status"] = status,
            ["updatedAt"] = statusAt.ToUnixTimeMilliseconds(),
            ["statusUpdatedAt"] = statusAt.ToUnixTimeMilliseconds(),
            ["bridgeSessionId"] = $"session_{pid}",
        };
        if (extra is not null)
            foreach (var (k, v) in extra) obj[k] = v?.DeepClone();
        return obj;
    }

    /// Every PID alive, all claiming the same start the fixtures write
    private static ProcessProbe AllAlive => new(_ => Started);

    private ClaudeFleetStore Store(ProcessProbe? probe = null) => new(_dir, probe ?? AllAlive);

    [Fact]
    public void ReadsAWaitingSessionWithWaitingFor()
    {
        Write(Record(100, "waiting", Now.AddSeconds(-840), new JsonObject { ["waitingFor"] = "input needed" }), 100);
        var snap = Store().Scan(Now);
        Assert.Single(snap.Sessions);
        var s = snap.Sessions[0];
        Assert.Equal(FleetState.Waiting, s.State);
        Assert.Equal("input needed", s.WaitingFor);
        Assert.Equal("proj-100", s.Folder);
        Assert.Equal("proj-100", s.Label);
        Assert.Equal(840, s.TimeInStatus(Now) is { } t ? (int)t : (int?)null);
        Assert.Equal("https://claude.ai/code/session_100", s.ClaudeUrl?.AbsoluteUri);
        Assert.Single(snap.Waiting);
    }

    [Fact]
    public void UnknownEntrypointAndUnknownFieldsSurvive()
    {
        Write(Record(101, "busy", Now, new JsonObject
        {
            ["entrypoint"] = "desktop",
            ["kind"] = "headless",
            ["someFutureField"] = new JsonObject { ["nested"] = true },
            ["peerProtocol"] = 7,
        }), 101);
        var snap = Store().Scan(Now);
        Assert.Single(snap.Sessions);
        Assert.Equal("desktop", snap.Sessions[0].Entrypoint);
        Assert.Equal("headless", snap.Sessions[0].Kind);
        Assert.Equal(FleetState.Busy, snap.Sessions[0].State);
    }

    [Fact]
    public void AnUnknownStatusStillShowsTheSession()
    {
        Write(Record(102, "compacting", Now), 102);
        var snap = Store().Scan(Now);
        Assert.Single(snap.Sessions);
        Assert.Equal(FleetState.Unknown, snap.Sessions[0].State);
        Assert.Equal("compacting", snap.Sessions[0].Status);
    }

    [Fact]
    public void MalformedRecordDoesNotTakeOutItsNeighbours()
    {
        Write(Record(200, "idle", Now), 200);
        File.WriteAllText(Path.Combine(_dir, "201.json"), "{not json at all");
        // Valid JSON, but missing the two fields a row cannot be drawn without
        Write(new JsonObject { ["status"] = "busy" }, 202);
        var snap = Store().Scan(Now);
        Assert.Equal(new[] { 200 }, snap.Sessions.Select(s => s.Pid));
    }

    [Fact]
    public void DeadPidIsTreatedAsAbsentAndTheFileIsLeftAlone()
    {
        Write(Record(300, "waiting", Now), 300);
        var snap = new ClaudeFleetStore(_dir, new ProcessProbe(_ => null)).Scan(Now);
        Assert.True(snap.IsEmpty);
        Assert.True(File.Exists(Path.Combine(_dir, "300.json")));
    }

    /// Claude Code writes procStart in UTC while naming no zone, so reading it as local rejected
    /// every live session west of Greenwich. Both readings count.
    [Fact]
    public void ProcStartWrittenInUTCIsAccepted()
    {
        var obj = Record(302, "busy", Now);
        obj["procStart"] = Ctime(Started, utc: true);
        Write(obj, 302);
        Assert.Equal(new[] { 302 }, Store().Scan(Now).Sessions.Select(s => s.Pid));
    }

    /// PIDs are reused, so a record whose claimed start does not match the live process is a
    /// leftover pointing at somebody else's process
    [Fact]
    public void RecycledPidIsRejected()
    {
        Write(Record(301, "busy", Now), 301);
        var other = Started.AddSeconds(3600);
        var snap = new ClaudeFleetStore(_dir, new ProcessProbe(_ => other)).Scan(Now);
        Assert.True(snap.IsEmpty);
    }

    [Fact]
    public void SortsWaitingFirstThenLongestInStatus()
    {
        Write(Record(1, "idle", Now.AddSeconds(-9000)), 1);
        Write(Record(2, "busy", Now.AddSeconds(-60)), 2);
        Write(Record(3, "waiting", Now.AddSeconds(-300)), 3);
        Write(Record(4, "waiting", Now.AddSeconds(-3600)), 4);
        Write(Record(5, "busy", Now.AddSeconds(-600)), 5);
        var snap = Store().Scan(Now);
        Assert.Equal(new[] { 4, 3, 5, 2, 1 }, snap.Sessions.Select(s => s.Pid));
    }

    [Fact]
    public void KeyFilesAreNeverRead()
    {
        Write(Record(400, "idle", Now), 400);
        File.WriteAllText(Path.Combine(_dir, "400.abc123.key"), "supersecret");
        var snap = Store().Scan(Now);
        Assert.Single(snap.Sessions);
    }

    [Fact]
    public void MissingDirectoryIsNotAnError()
    {
        Assert.True(new ClaudeFleetStore(Path.Combine(_dir, "nope")).Scan().IsEmpty);
    }

    [Fact]
    public void NameFallsBackToTheFolder()
    {
        var obj = Record(500, "busy", Now);
        obj.Remove("name");
        obj.Remove("statusUpdatedAt");
        obj.Remove("updatedAt");
        Write(obj, 500);
        var snap = Store().Scan(Now);
        Assert.Equal("proj-500", snap.Sessions[0].Label);
        Assert.Null(snap.Sessions[0].TimeInStatus(Now));
    }

    /// Windows has no controlling terminal device, so the probe answers "none" for any PID,
    /// which is the macOS answer for a process without a tty.
    [Fact]
    public void TTYPathIsADeviceOrNothing()
    {
        var tty = ProcessProbe.TtyPath(Environment.ProcessId);
        if (tty is not null)
        {
            Assert.StartsWith("/dev/", tty);
            Assert.True(tty.Length > 5);
        }
        Assert.Null(ProcessProbe.TtyPath(int.MaxValue));
    }

    /// The real probe, against this very process: a start time that exists and is not absurd
    [Fact]
    public void LiveProbeAnswersForThisProcess()
    {
        var start = ProcessProbe.Live.StartTime(Environment.ProcessId);
        Assert.NotNull(start);
        if (start is { } s) Assert.True(Math.Abs((DateTimeOffset.UtcNow - s).TotalSeconds) < 86400);
        Assert.Null(ProcessProbe.Live.StartTime(int.MaxValue));
    }
}
