// What the timestamps say about how the work is spread out: stretch, late and streak. A cue
// states a measured fact and stops; nothing here infers tiredness or gives advice.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Redline.Core;

/// A run of activity with no gap longer than `gap` in it.
public sealed record Stretch(DateTimeOffset Start, DateTimeOffset End, int Records)
{
    /// Measured from first activity to last, never to now: a pause is not work.
    public double Length => (End - Start).TotalSeconds;

    /// Stable across polls while the stretch continues, which is what "say this once" needs.
    public string Id => $"stretch|{Start.ToUnixTimeSeconds()}";

    public bool IsOpen(DateTimeOffset now, double gap) => (now - End).TotalSeconds <= gap;
}

public static class Cadence
{
    /// What counts as a break: long enough that a slow turn does not split a stretch, short
    /// enough that a coffee does.
    public const double DefaultGap = 900;

    /// Every run of activity, oldest first.
    public static List<Stretch> Stretches(IEnumerable<Entry> entries, double gap = DefaultGap)
    {
        var times = entries.Select(e => e.Ts).OrderBy(t => t).ToList();
        if (times.Count == 0) return new List<Stretch>();
        var output = new List<Stretch>();
        var start = times[0];
        var last = times[0];
        var count = 0;
        foreach (var ts in times)
        {
            if ((ts - last).TotalSeconds > gap)
            {
                output.Add(new Stretch(start, last, count));
                start = ts;
                count = 0;
            }
            last = ts;
            count++;
        }
        output.Add(new Stretch(start, last, count));
        return output;
    }

    /// The stretch still in progress, or null when the run has ended.
    public static Stretch? Current(IEnumerable<Entry> entries, double gap = DefaultGap,
                                  DateTimeOffset? now = null)
    {
        var last = Stretches(entries, gap).LastOrDefault();
        return last is not null && last.IsOpen(now ?? DateTimeOffset.UtcNow, gap) ? last : null;
    }

    /// Local days that saw any activity, as the instant each began, oldest first. Local
    /// because a streak is a claim about days as the person at the keyboard lived them.
    public static List<DateTimeOffset> ActiveDays(IEnumerable<Entry> entries, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        return entries.Select(e => StartOfDay(e.Ts, tz)).Distinct().OrderBy(d => d).ToList();
    }

    /// Consecutive active days ending at `endingOn`, counting that day only if it is active.
    public static int Streak(IEnumerable<Entry> entries, DateTimeOffset? endingOn = null,
                             TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var days = entries.Select(e => LocalDate(e.Ts, tz)).ToHashSet();
        var day = LocalDate(endingOn ?? DateTimeOffset.UtcNow, tz);
        var count = 0;
        while (days.Contains(day))
        {
            count++;
            day = day.AddDays(-1);
        }
        return count;
    }

    /// Tokens by local hour of day, 24 buckets starting at midnight.
    public static int[] ByHourOfDay(IEnumerable<Entry> entries, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var output = new int[24];
        foreach (var e in entries)
        {
            var hour = TimeZoneInfo.ConvertTime(e.Ts, tz).Hour;
            output[hour] += e.Input + e.Output;
        }
        return output;
    }

    /// The night a moment belongs to. Nights roll at 05:00, so 01:30 belongs to the evening
    /// that ran into it. Spelled as Swift does: the local day's start, formatted in UTC.
    internal static string NightKey(DateTimeOffset date, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var day = StartOfDay(date.AddHours(-5), tz);
        return day.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    internal static DateOnly LocalDate(DateTimeOffset t, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, tz).DateTime);

    /// The first instant of the local day holding `t`, skipping forward past a DST gap.
    internal static DateTimeOffset StartOfDay(DateTimeOffset t, TimeZoneInfo tz)
    {
        var midnight = TimeZoneInfo.ConvertTime(t, tz).Date;
        var local = midnight;
        while (tz.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }
}

/// What a cue is about.
public abstract record CadenceCueKind
{
    /// A run of activity has passed a multiple of the reader's threshold.
    public sealed record Stretch(double Length) : CadenceCueKind;
    /// Activity after the hour the reader nominated.
    public sealed record Late(int Hour) : CadenceCueKind;
    /// Consecutive days with activity.
    public sealed record Streak(int Days) : CadenceCueKind;
}

/// One thing worth saying, once. `Id` is stable per cue for de-duplication.
public sealed record CadenceCue(CadenceCueKind Kind, string Title, string Body, string Id);

/// What has already been said. A new stretch, a new night and a longer streak each re-arm.
public sealed class CadenceState : IEquatable<CadenceState>
{
    /// The stretch the last stretch cue was said for, and how many thresholds of it fired.
    public string StretchID { get; set; } = "";
    public int StretchFired { get; set; }
    /// The night the last late cue was said for.
    public string LastLateNight { get; set; } = "";
    /// Highest streak multiple already announced.
    public int StreakFired { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public bool Equals(CadenceState? o) => o is not null && StretchID == o.StretchID &&
        StretchFired == o.StretchFired && LastLateNight == o.LastLateNight &&
        StreakFired == o.StreakFired && UpdatedAt == o.UpdatedAt;
    public override bool Equals(object? obj) => Equals(obj as CadenceState);
    public override int GetHashCode() => HashCode.Combine(StretchID, StretchFired, LastLateNight, StreakFired);
}

public static class CadenceRules
{
    /// Cues for one poll's worth of entries. Passing less history does not produce a wrong
    /// answer, it produces a smaller one, which is the right way round for this.
    public static List<CadenceCue> Evaluate(IReadOnlyList<Entry> entries, Config config, CadenceState state,
                                            DateTimeOffset? now = null, TimeZoneInfo? timeZone = null)
    {
        if (!config.MindfulCues) return new List<CadenceCue>();
        if (entries.Count == 0) return new List<CadenceCue>();
        var t = now ?? DateTimeOffset.UtcNow;
        var tz = timeZone ?? TimeZoneInfo.Local;
        var cues = new List<CadenceCue>();
        state.UpdatedAt = t;

        // Stretch: announced at each multiple of the threshold, not once and then never
        var threshold = Math.Max(15, config.StretchMinutes) * 60;
        if (Cadence.Current(entries, now: t) is { } stretch)
        {
            if (state.StretchID != stretch.Id)
            {
                state.StretchID = stretch.Id;
                state.StretchFired = 0;
            }
            var reached = (int)(stretch.Length / threshold);
            if (reached > state.StretchFired)
            {
                state.StretchFired = reached;
                cues.Add(new CadenceCue(new CadenceCueKind.Stretch(stretch.Length),
                    $"{Pace.Short(stretch.Length)} at this",
                    $"Since {Clock(stretch.Start, tz)}, with no gap longer than {Pace.Short(Cadence.DefaultGap)}.",
                    $"{stretch.Id}|{reached}"));
            }
        }
        else if (state.StretchID.Length > 0)
        {
            // The run ended. Nothing is said about that; it just re-arms.
            state.StretchID = "";
            state.StretchFired = 0;
        }

        // Late: one per night, and only when the activity is recent enough to be happening
        var last = entries.Max(e => e.Ts);
        if ((t - last).TotalSeconds <= Cadence.DefaultGap)
        {
            var hour = TimeZoneInfo.ConvertTime(last, tz).Hour;
            var lateHour = Math.Min(23, Math.Max(18, config.LateHour));
            if (hour >= lateHour || hour < 5)
            {
                var night = Cadence.NightKey(last, tz);
                if (state.LastLateNight != night)
                {
                    state.LastLateNight = night;
                    var dayStart = Cadence.StartOfDay(last, tz);
                    var todays = entries.Select(e => e.Ts).Where(ts => ts >= dayStart).ToList();
                    var body = "Still going.";
                    if (todays.Count > 0 && todays.Min() is var first && first < last)
                        body = $"First activity today was {Clock(first, tz)}.";
                    cues.Add(new CadenceCue(new CadenceCueKind.Late(hour),
                        $"It is {Clock(last, tz)}", body, $"late|{night}"));
                }
            }
        }

        // Streak: each multiple of the threshold, since a daily "another day" is noise
        var streakThreshold = Math.Max(2, config.StreakDays);
        var streak = Cadence.Streak(entries, t, tz);
        var multiple = streak / streakThreshold;
        if (multiple > state.StreakFired && streak >= streakThreshold)
        {
            state.StreakFired = multiple;
            var days = Cadence.ActiveDays(entries, tz);
            var body = "Every day counted here has had usage.";
            if (days.Count > 0)
            {
                var since = days[Math.Max(0, days.Count - streak)];
                body = $"Every day since {Date(since, tz)} has had usage.";
            }
            cues.Add(new CadenceCue(new CadenceCueKind.Streak(streak), $"{streak} days running",
                body, $"streak|{streak}"));
        }
        else if (streak < streakThreshold)
        {
            state.StreakFired = 0;
        }

        return cues;
    }

    /// A short local time in the reader's own format, "9:05 PM" or "21:05".
    internal static string Clock(DateTimeOffset date, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(date, tz).ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern,
            CultureInfo.CurrentCulture);

    /// Abbreviated month and day in the reader's own order, "Aug 18".
    internal static string Date(DateTimeOffset date, TimeZoneInfo tz)
    {
        var pattern = CultureInfo.CurrentCulture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM");
        return TimeZoneInfo.ConvertTime(date, tz).ToString(pattern, CultureInfo.CurrentCulture);
    }
}

/// Where the cue state lives: its own file, because it is a record of what happened.
public static class CadenceStore
{
    public static string PathFor(string? home = null) =>
        Path.Combine(RedlineHome.DataDir(home), "cadence.json");

    public static CadenceState Load(string? path = null)
    {
        path ??= PathFor();
        return StateFile.Load(path, "cadence.state_corrupt", "state file did not decode; starting over",
            Decode, () => new CadenceState());
    }

    public static bool Save(CadenceState state, string? path = null)
    {
        path ??= PathFor();
        var json = new JsonObject
        {
            ["lastLateNight"] = state.LastLateNight,
            ["streakFired"] = state.StreakFired,
            ["stretchFired"] = state.StretchFired,
            ["stretchID"] = state.StretchID,
        };
        if (state.UpdatedAt is { } u) json["updatedAt"] = StateFile.Stamp(u);
        return StateFile.Save(path, json, "cadence.save_failed", "could not write state");
    }

    private static CadenceState Decode(JsonObject json) => new()
    {
        StretchID = Json.Str(json["stretchID"]) ?? throw new FormatException("stretchID missing"),
        StretchFired = Json.Int(json["stretchFired"]) ?? throw new FormatException("stretchFired missing"),
        LastLateNight = Json.Str(json["lastLateNight"]) ?? throw new FormatException("lastLateNight missing"),
        StreakFired = Json.Int(json["streakFired"]) ?? throw new FormatException("streakFired missing"),
        UpdatedAt = json["updatedAt"] is null ? null : StateFile.ParseDate(json["updatedAt"]),
    };
}
