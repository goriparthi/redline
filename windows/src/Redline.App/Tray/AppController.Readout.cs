// The tray readout: what renderTitle, applyFleetBadge and menuBarMarkTint decided, redrawn
// as one icon plus a tooltip, since the notification area has no room for text.
using System.Globalization;
using System.Windows.Media;
using Redline.App.Services;
using Redline.Core;

namespace Redline.App.Tray;

public sealed partial class AppController
{
    void UpdateTitle()
    {
        if (tray is null) return;
        var r = ComputeReadout();
        // Split: a square tray icon has room for one full-height number, so the week gets its own
        if (config.TrayLayout == "split" && r.Lower is { } week)
        {
            // Both icons are sized as the widest case, so the pair reads as one type size
            var fit = new string('8', Math.Max(r.Number?.Length ?? 0, week.Length));
            tray.Show(r with { Lower = null, Suffix = "S", FitAs = fit });
            if (weekTray is null)
            {
                weekTray = new TrayIcon();
                weekTray.Clicked += OnTrayClicked;
            }
            weekTray.Show(new TrayReadout(week, r.LowerColor, null, 1, false, false, r.Tooltip, Suffix: "W", FitAs: fit));
            return;
        }
        weekTray?.Dispose();
        weekTray = null;
        tray.Show(r);
    }

    TrayReadout ComputeReadout()
    {
        string? number = null, lower = null;
        var numberColor = MenuInk.Primary;
        var lowerColor = MenuInk.Primary;
        var needsConnect = false;
        string tooltip;
        switch (config.MenuBarDisplay)
        {
            case "limits":
                if (LimitsTitle() is { } title)
                {
                    number = Pct(title.Lead).ToString(CultureInfo.InvariantCulture);
                    numberColor = WindowColor(title.Lead);
                    tooltip = title.Text + "\n" + LimitsTooltip();
                    // Claude has both a session and a week worth watching: session over week
                    if (StackedWindows(title.Lead.Provider) is var (s5, wk))
                    {
                        (number, numberColor) = (Pct(s5).ToString(CultureInfo.InvariantCulture), WindowColor(s5));
                        (lower, lowerColor) = (Pct(wk).ToString(CultureInfo.InvariantCulture), WindowColor(wk));
                    }
                    break;
                }
                // A provider with no limits by nature reports volume, and only in words: never a percentage
                if (config.MenuBarProvider != Config.AutoProvider && config.MenuBarProvider.Length > 0 &&
                    MenuBarLimits.Count == 0 && today.Providers.TryGetValue(CanonicalMenuBarProvider, out var usage))
                {
                    tooltip = $"{Usage.FmtTokens(usage.Io)}\n{CanonicalMenuBarProvider} · tokens today, no limit reported";
                    break;
                }
                // Never fall back to tokens or cost here: it reads as a real limit figure
                var signedIn = oauth.IsSignedIn;
                // A source merely between readings is pending; Signal red is for the state a click fixes
                var pending = signedIn || config.UseCLIToken || StatuslineInstaller.IsWanted();
                needsConnect = !pending;
                tooltip = signedIn ? "Usage loading…"
                    : config.UseCLIToken ? "Claude Code's token is not readable; open RedLine to fix it"
                    : StatuslineInstaller.IsWanted() ? FeedNote
                    : "Connect Claude to view usage";
                break;
            case "cost":
                tooltip = $"{Usage.FmtCost(today.Cost)} today";
                break;
            case "tokens":
                tooltip = $"{Usage.FmtTokens(today.Io)} today";
                break;
            case "session":
                if (Worst("five_hour") is { } s)
                {
                    number = Pct(s).ToString(CultureInfo.InvariantCulture);
                    numberColor = WindowColor(s);
                    tooltip = $"{Pct(s)}% · {s.Provider} session (5h)";
                }
                else tooltip = $"{Usage.FmtCost(today.Cost)} today";
                break;
            default:
                tooltip = $"{Usage.FmtTokens(today.Io)} {Usage.FmtCost(today.Cost)} today";
                break;
        }

        // Only waiting earns the badge: a busy fleet is the ordinary state
        var waiting = config.AgentFleet ? fleet.Waiting : new List<FleetSession>();
        if (waiting.Count > 0)
        {
            var head = waiting.Count == 1 ? "1 agent waiting on you" : $"{waiting.Count} agents waiting on you";
            tooltip = $"{head}: {string.Join(", ", waiting.Select(w => w.Label))}\n{tooltip}";
        }

        var tint = MenuBarMarkTint();
        // The tray has room for one thing, and a real percentage beats the mark; the mark stands in
        // only while there is no limit figure to show
        return new TrayReadout(number, numberColor, tint?.Color, tint?.Alpha ?? 1, waiting.Count > 0, needsConnect && number is null,
                               tooltip, lower, lowerColor);
    }

    /// <summary>The mark carries the readout's state: colour survives a stale reading, faded.</summary>
    (Color Color, double Alpha)? MenuBarMarkTint()
    {
        List<LimitWindow> shown;
        switch (config.MenuBarDisplay)
        {
            case "limits":
                shown = new[] { WantsSessionWindow ? Worst("five_hour") : null, WantsWeekWindow ? Worst("seven_day") : null }
                    .OfType<LimitWindow>().ToList();
                break;
            case "session":
                shown = new[] { Worst("five_hour") }.OfType<LimitWindow>().ToList();
                break;
            default:
                return null;
        }
        if (shown.Count == 0) return null;
        var lead = shown.MaxBy(w => w.Utilization)!;
        return (LimitColor(lead.Utilization), IsStale(lead) ? 0.55 : 1);
    }

    /// <summary>In auto, the binding constraint is whichever provider is closest to its limit.</summary>
    List<LimitWindow> MenuBarLimits
    {
        get
        {
            var choice = config.MenuBarProvider;
            if (choice == Config.AutoProvider) return AllLimits.ToList();
            return AllLimits.Where(w => string.Equals(w.Provider, choice, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    /// <summary>Claude's session and week, both wanted and both present; other providers show one number.</summary>
    (LimitWindow Session, LimitWindow Week)? StackedWindows(string provider)
    {
        if (provider != UsageStore.Provider || !WantsSessionWindow || !WantsWeekWindow) return null;
        var mine = MenuBarLimits.Where(w => w.Provider == provider).ToList();
        var s = mine.Where(w => w.Key.StartsWith("five_hour", StringComparison.Ordinal)).MaxBy(w => w.Utilization);
        var wk = mine.Where(w => w.Key.StartsWith("seven_day", StringComparison.Ordinal)).MaxBy(w => w.Utilization);
        return s is not null && wk is not null ? (s, wk) : null;
    }

    LimitWindow? Worst(params string[] keys) =>
        MenuBarLimits.Where(w => keys.Any(k => w.Key.StartsWith(k, StringComparison.Ordinal)))
                     .MaxBy(w => w.Utilization);

    static int Pct(LimitWindow w) => (int)Math.Round(w.Utilization, MidpointRounding.AwayFromZero);

    bool WantsSessionWindow => config.LimitWindows != "week";
    bool WantsWeekWindow => config.LimitWindows != "session";

    bool WantsWindow(LimitWindow w)
    {
        if (w.Key.StartsWith("five_hour", StringComparison.Ordinal)) return WantsSessionWindow;
        if (w.Key.StartsWith("seven_day", StringComparison.Ordinal)) return WantsWeekWindow;
        return true;
    }

    /// <summary>Past this age every surface drains Claude's windows to steel.</summary>
    bool ClaudeLimitsAreStale =>
        claudeLimitsAt is { } at && (DateTimeOffset.UtcNow - at).TotalSeconds > Math.Max(config.PollIntervalSeconds * 2, 600);

    bool IsStale(LimitWindow w) => w.Provider == UsageStore.Provider && ClaudeLimitsAreStale;

    Color LimitColor(double pct) => RL.BrandTone.Of(Brand.Status(pct, config.LimitYellowPct, config.LimitRedPct).Color());

    /// <summary>Status colour while live, steel once not. The number stays; the palette says "as of earlier".</summary>
    Color WindowColor(LimitWindow w) => IsStale(w) ? RL.BrandTone.Steel : LimitColor(w.Utilization);

    /// <summary>The menu bar title as words: "45% 2p | 5% Tue 1a", and the window the icon's number shows.</summary>
    (string Text, LimitWindow Lead)? LimitsTitle()
    {
        var session = WantsSessionWindow ? Worst("five_hour") : null;
        var wk = WantsWeekWindow ? Worst("seven_day") : null;
        if (session is null && wk is null) return null;
        var parts = new List<string>();
        if (session is not null)
        {
            var p = $"{Pct(session)}%";
            if (config.ShowResetTimes && FmtResetShort(session.ResetsAt) is { } r) p += $" {r}";
            parts.Add(p);
        }
        if (wk is not null)
        {
            var p = $"{Pct(wk)}%";
            if (config.ShowResetTimes && FmtResetShort(wk.ResetsAt) is { } r) p += $" {r}";
            parts.Add(p);
        }
        var lead = new[] { session, wk }.OfType<LimitWindow>().MaxBy(w => w.Utilization)!;
        var stale = IsStale(lead) && claudeLimitsAt is { } at ? $" (as of {at.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture)})" : "";
        return (string.Join(" | ", parts) + stale, lead);
    }

    /// <summary>Config stores whatever case the user typed, so resolve to the canonical spelling.</summary>
    string CanonicalMenuBarProvider =>
        Config.KnownProviders.FirstOrDefault(p => string.Equals(p, config.MenuBarProvider, StringComparison.OrdinalIgnoreCase))
        ?? config.MenuBarProvider;

    string LimitsTooltip()
    {
        if (config.MenuBarProvider != Config.AutoProvider) return $"{CanonicalMenuBarProvider} · session (5h) | week";
        var names = string.Join(", ", MenuBarLimits.Select(w => w.Provider).Distinct().OrderBy(n => n, StringComparer.Ordinal));
        return names.Length == 0 ? "Session (5h) | week" : $"Nearest limit across: {names}";
    }

    /// <summary>Compact reset stamp: "3p" or "3:30p" today, "Tue 9a" otherwise.</summary>
    static string? FmtResetShort(DateTimeOffset? d)
    {
        if (d is not { } date) return null;
        var local = date.ToLocalTime();
        var ampm = local.Hour < 12 ? "a" : "p";
        var h = local.ToString(local.Minute == 0 ? "%h" : "h:mm", CultureInfo.InvariantCulture) + ampm;
        return local.Date == DateTime.Now.Date ? h : local.ToString("ddd ", CultureInfo.InvariantCulture) + h;
    }

    /// <summary>", resets 2:10 PM" or ", resets Tue 1:00 AM", as the dropdown rows read.</summary>
    static string FmtReset(DateTimeOffset? d)
    {
        if (d is not { } date) return "";
        var local = date.ToLocalTime();
        var fmt = local.Date == DateTime.Now.Date ? "h:mm tt" : "ddd h:mm tt";
        return ", resets " + local.ToString(fmt, CultureInfo.InvariantCulture);
    }
}
