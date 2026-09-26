// The desktop widget: a borderless, draggable window rendering the snapshot, optionally held
// behind every other window. Several can run at once, each with its own track and size.
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Redline.Core;

namespace Redline.App.Tray;

public sealed class WidgetWindow : Window
{
    public WidgetSpec Spec { get; private set; }
    Snapshot? snapshot;
    readonly WidgetManager owner;

    public WidgetWindow(WidgetSpec spec, WidgetManager owner)
    {
        Spec = spec;
        this.owner = owner;
        Title = "RedLine widget";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Left = spec.Left;
        Top = spec.Top;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { }
            Spec = Spec with { Left = Left, Top = Top };
            owner.Save();
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // A tool window stays out of Alt+Tab, which a widget has no business in
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
            ApplyLayer();
        };
        Activated += (_, _) => ApplyLayer();
        ContextMenu = new ContextMenu();
        Fill(ContextMenu);
        ContextMenuOpening += (_, _) => Fill(ContextMenu);
    }

    public void Render(Snapshot? snap)
    {
        snapshot = snap;
        Content = WidgetView.Build(snap, Spec.Track, Spec.Family);
    }

    public void Rerender() => Render(snapshot);

    void Change(WidgetSpec next)
    {
        Spec = next;
        Topmost = false;
        Rerender();
        ApplyLayer();
        owner.Save();
    }

    void Fill(ContextMenu menu)
    {
        menu.Items.Clear();
        var track = new MenuItem { Header = "Track" };
        foreach (var t in Enum.GetValues<TrackChoice>())
        {
            var item = new MenuItem { Header = t.Title(), IsCheckable = true, IsChecked = Spec.Track == t };
            item.Click += (_, _) => Change(Spec with { Track = t });
            track.Items.Add(item);
        }
        menu.Items.Add(track);
        var size = new MenuItem { Header = "Size" };
        foreach (var f in Enum.GetValues<WidgetFamily>())
        {
            var item = new MenuItem { Header = f.ToString(), IsCheckable = true, IsChecked = Spec.Family == f };
            item.Click += (_, _) => Change(Spec with { Family = f });
            size.Items.Add(item);
        }
        menu.Items.Add(size);
        var bottom = new MenuItem { Header = "Keep Behind Windows", IsCheckable = true, IsChecked = Spec.OnBottom };
        bottom.Click += (_, _) => Change(Spec with { OnBottom = !Spec.OnBottom });
        menu.Items.Add(bottom);
        menu.Items.Add(new Separator());
        var dash = new MenuItem { Header = "Open Usage Dashboard..." };
        dash.Click += (_, _) => owner.OpenDashboard();
        menu.Items.Add(dash);
        var another = new MenuItem { Header = "Add Another Widget" };
        another.Click += (_, _) => owner.Add(Spec.Track, Spec.Family);
        menu.Items.Add(another);
        var remove = new MenuItem { Header = "Remove Widget" };
        remove.Click += (_, _) => owner.Remove(Spec.Id);
        menu.Items.Add(remove);
    }

    // Held behind every other window while OnBottom; otherwise an ordinary window
    void ApplyLayer()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Spec.OnBottom) return;
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING && Spec.OnBottom)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x80;
    const int WM_WINDOWPOSCHANGING = 0x0046;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_BOTTOM = new(1);

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
