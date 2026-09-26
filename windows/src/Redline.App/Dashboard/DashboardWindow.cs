// The usage dashboard window (DashboardView plus the window AppDelegate builds in Swift): a
// scroller around DashboardContent, themed by the dashboard's own appearance choice.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Redline.Core;

namespace Redline.App.Dashboard;

public sealed class DashboardWindow : Window
{
    public DashboardModel Model { get; }
    public DashboardContent Dashboard { get; }

    /// <param name="onReload">The range buttons and Rescan; defaults to model.Load(days, current limits).</param>
    /// <param name="onFocus">The provider picker and cards; defaults to model.SetFocus.</param>
    /// <param name="onOpenSettings">Shows the gear button when set.</param>
    public DashboardWindow(DashboardModel model, Action<int>? onReload = null, Action<string>? onFocus = null,
                           Action? onOpenSettings = null)
    {
        Model = model;
        Title = "RedLine Usage";
        Width = 1000;
        Height = 820;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/RedLine;component/Assets/RedLine.ico")); }
        catch (Exception) { }
        Dashboard = new DashboardContent(model,
            onReload ?? (days => model.Load(days, model.Data.Limits)),
            onFocus ?? model.SetFocus,
            onOpenSettings);
        Content = new ScrollViewer
        {
            Content = Dashboard, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        Background = RL.Surface.Ground.Brush(Dashboard.EffectiveTheme);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this, Dashboard.EffectiveTheme);
        // Loaded content is re-themed on every rebuild; this catches the first paint
        Loaded += (_, _) => Dashboard.Rebuild();
    }

    /// <summary>Renders the whole scroll content, not just the viewport, to a PNG.</summary>
    public void SaveSnapshot(string path, double scale = 1)
    {
        var el = Dashboard;
        el.UpdateLayout();
        var w = el.ActualWidth;
        var h = el.ActualHeight;
        var dpi = VisualTreeHelper.GetDpi(this);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(el.Background, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h));
        }
        var sx = dpi.DpiScaleX * scale;
        var sy = dpi.DpiScaleY * scale;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * sx), (int)Math.Ceiling(h * sy), 96 * sx, 96 * sy, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }

    /// <summary>Waits for layout and the coalesced rebuilds, snapshots, then closes.</summary>
    public void SaveSnapshotAndClose(string path) =>
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            SaveSnapshot(path);
            Close();
        });
}
