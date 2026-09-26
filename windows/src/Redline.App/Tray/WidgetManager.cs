// Every desktop widget this profile has, restored at launch and saved on each change to
// ~/.config/redline/widgets.json. They all render the one snapshot the controller publishes.
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Redline.Core;

namespace Redline.App.Tray;

public sealed record WidgetSpec(string Id, TrackChoice Track, WidgetFamily Family, double Left, double Top, bool OnBottom);

public sealed class WidgetManager
{
    readonly List<WidgetWindow> windows = new();
    readonly Action openDashboard;
    readonly DispatcherTimer tick;
    Snapshot? snapshot;

    public static string StorePath => RedlineHome.PathFor(".config/redline/widgets.json");

    public WidgetManager(Action openDashboard)
    {
        this.openDashboard = openDashboard;
        // A widget labels itself stale by age, so it redraws even when nothing new arrives
        tick = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => Rerender(),
                                   Application.Current.Dispatcher);
    }

    public IReadOnlyList<WidgetSpec> Widgets => windows.Select(w => w.Spec).ToList();

    public void OpenDashboard() => openDashboard();

    public void Restore(Snapshot? current)
    {
        snapshot = current;
        foreach (var spec in Load()) Open(spec);
        if (windows.Count > 0) tick.Start();
    }

    public void Update(Snapshot? next)
    {
        snapshot = next;
        foreach (var w in windows) w.Render(next);
    }

    void Rerender()
    {
        foreach (var w in windows) w.Rerender();
    }

    public void Add(TrackChoice track, WidgetFamily family = WidgetFamily.Medium)
    {
        var size = family.Size();
        var work = SystemParameters.WorkArea;
        // Cascaded from the top right, so a new widget never lands exactly on an old one
        var offset = windows.Count * 24.0;
        var spec = new WidgetSpec(Guid.NewGuid().ToString("N")[..12], track, family,
            Math.Max(work.Left, work.Right - size.Width - 24 - offset), work.Top + 24 + offset, OnBottom: true);
        Open(spec);
        tick.Start();
        Save();
    }

    public void Remove(string id)
    {
        foreach (var w in windows.Where(w => w.Spec.Id == id).ToList())
        {
            windows.Remove(w);
            w.Close();
        }
        if (windows.Count == 0) tick.Stop();
        Save();
    }

    public void RemoveAll()
    {
        foreach (var w in windows.ToList()) w.Close();
        windows.Clear();
        tick.Stop();
        Save();
    }

    public void CloseAll()
    {
        foreach (var w in windows.ToList()) w.Close();
        windows.Clear();
        tick.Stop();
    }

    void Open(WidgetSpec spec)
    {
        var w = new WidgetWindow(OnScreen(spec), this);
        w.Render(snapshot);
        windows.Add(w);
        w.Show();
    }

    /// <summary>A widget saved on a monitor that is gone comes back to the primary work area.</summary>
    static WidgetSpec OnScreen(WidgetSpec spec)
    {
        var size = spec.Family.Size();
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        if (spec.Left + 40 > left && spec.Left + size.Width - 40 < right && spec.Top + 20 > top && spec.Top + 40 < bottom) return spec;
        var work = SystemParameters.WorkArea;
        return spec with { Left = work.Right - size.Width - 24, Top = work.Top + 24 };
    }

    public void Save()
    {
        var list = new JsonArray(windows.Select(w => (JsonNode)new JsonObject
        {
            ["id"] = w.Spec.Id,
            ["track"] = w.Spec.Track.RawValue(),
            ["size"] = w.Spec.Family.RawValue(),
            ["left"] = Math.Round(w.Spec.Left, 1),
            ["top"] = Math.Round(w.Spec.Top, 1),
            ["onBottom"] = w.Spec.OnBottom,
        }).ToArray());
        try { Json.WriteAtomic(StorePath, new JsonObject { ["widgets"] = list }.ToJsonString(Json.Pretty)); }
        catch (Exception e)
        {
            Diag.Log.Error("widgets.write_failed", "could not save the desktop widgets",
                new() { ["path"] = StorePath, ["error"] = e.Message });
        }
    }

    public static List<WidgetSpec> Load(string? path = null)
    {
        var output = new List<WidgetSpec>();
        if (Json.ReadObject(path ?? StorePath)?["widgets"] is not JsonArray list) return output;
        foreach (var node in list.OfType<JsonObject>())
        {
            var track = Enum.TryParse<TrackChoice>(Json.Str(node["track"]), true, out var t) ? t : TrackChoice.All;
            var family = Enum.TryParse<WidgetFamily>(Json.Str(node["size"]), true, out var f) ? f : WidgetFamily.Medium;
            output.Add(new WidgetSpec(Json.Str(node["id"]) ?? Guid.NewGuid().ToString("N")[..12], track, family,
                Json.Num(node["left"]) ?? 0, Json.Num(node["top"]) ?? 0, Json.Bool(node["onBottom"]) ?? true));
        }
        return output;
    }
}
