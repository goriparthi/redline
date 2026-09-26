// The notification-area icon. Windows cannot put text there, so the readout is drawn into the
// icon itself at the size the taskbar asks for, and the full wording rides in the tooltip.
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Redline.App.Tray;

/// <summary>What the icon draws. A number wins, stacked over `Lower` when there are two windows;
/// otherwise the mark, tinted when a reading exists.</summary>
public sealed record TrayReadout(string? Number, Color NumberColor, Color? MarkTint, double MarkAlpha,
                                 bool Waiting, bool NeedsConnect, string Tooltip,
                                 string? Lower = null, Color LowerColor = default, string Suffix = "%",
                                 string? FitAs = null);

public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon icon = new();
    System.Drawing.Icon? current;
    TrayReadout? last;

    public event Action<Forms.MouseButtons>? Clicked;

    public TrayIcon()
    {
        icon.Text = "RedLine";
        icon.MouseUp += (_, e) => Clicked?.Invoke(e.Button);
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    public void Show(TrayReadout readout)
    {
        last = readout;
        var size = IconSize();
        var next = Render(readout, size, TaskbarIsLight());
        icon.Icon = next;
        current?.Dispose();
        current = next;
        icon.Text = Clip(readout.Tooltip);
        icon.Visible = true;
    }

    /// <summary>The shell icon itself, for the notifier that shows balloons through it.</summary>
    public Forms.NotifyIcon NotifyIcon => icon;

    // NotifyIcon refuses more than 127 characters
    static string Clip(string text, int max = 127) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || last is null) return;
        Application.Current?.Dispatcher.BeginInvoke(() => { if (last is not null) Show(last); });
    }

    void OnDisplayChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(() => { if (last is not null) Show(last); });

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        icon.Visible = false;
        icon.Dispose();
        current?.Dispose();
    }

    /// <summary>The small-icon size for the system DPI: 16, 20, 24 or 32 at 100 to 200 percent.</summary>
    public static int IconSize()
    {
        try
        {
            var dpi = GetDpiForSystem();
            var s = GetSystemMetricsForDpi(49, dpi); // SM_CXSMICON
            if (s > 0) return s;
        }
        catch { }
        return Forms.SystemInformation.SmallIconSize.Width;
    }

    /// <summary>The taskbar follows the system theme, not the apps theme.</summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>Status colours wash out on the taskbar, so they are pushed away from its ground.</summary>
    public static Color OnTaskbar(Color c, bool light) =>
        light ? MenuInk.Blend(c, Colors.Black, 0.35) : MenuInk.Blend(c, Colors.White, 0.25);

    public static System.Drawing.Icon Render(TrayReadout r, int size, bool lightTaskbar)
    {
        var bitmap = RenderBitmap(r, size, lightTaskbar);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = size * 4;
        var pixels = new byte[stride * size];
        converted.CopyPixels(pixels, stride, 0);
        using var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, size, size),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bmp.UnlockBits(data);
        var handle = bmp.GetHicon();
        // Cloned so the handle GetHicon made can be released at once
        using var owned = System.Drawing.Icon.FromHandle(handle);
        var copy = (System.Drawing.Icon)owned.Clone();
        DestroyIcon(handle);
        return copy;
    }

    public static BitmapSource RenderBitmap(TrayReadout r, int size, bool lightTaskbar)
    {
        var ink = lightTaskbar ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Colors.White;
        var ground = lightTaskbar ? Color.FromRgb(0xEE, 0xEE, 0xEE) : Color.FromRgb(0x1F, 0x1F, 0x1F);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (r.Number is { } n && r.Lower is { } lower)
            {
                // Session over week. Waiting tints the divider rather than covering a digit with a dot
                var ruleInk = r.Waiting ? OnTaskbar(RL.State.Warning.Dark, lightTaskbar) : ink;
                var ruleAlpha = 1.0;
                if (size < 24) DrawPixelPair(dc, n, lower, OnTaskbar(r.NumberColor, lightTaskbar),
                                             OnTaskbar(r.LowerColor, lightTaskbar), ruleInk, ruleAlpha, size);
                else
                {
                    // Height is what the digits need; the rule and its air take one pixel each until 32 px
                    var rule = size >= 32 ? 2.0 : 1.0;
                    var air = size >= 32 ? 2.0 : 1.0;
                    var band = (size - rule - 2 * air) / 2.0;
                    DrawNumber(dc, n, OnTaskbar(r.NumberColor, lightTaskbar), size, 0, band, percent: false);
                    DrawNumber(dc, lower, OnTaskbar(r.LowerColor, lightTaskbar), size, band + 2 * air + rule, band, percent: false);
                    dc.DrawRectangle(new SolidColorBrush(ruleInk) { Opacity = ruleAlpha }, null,
                                     new Rect(Math.Round(size * 0.25), Math.Floor(band + air), Math.Round(size * 0.5), rule));
                }
            }
            else if (r.Number is { } n1)
                DrawNumber(dc, n1, OnTaskbar(r.NumberColor, lightTaskbar), size, 0, size, percent: true, r.Suffix, r.FitAs,
                           lightTaskbar ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xE8, 0xE8, 0xE8));
            else
            {
                var tint = r.MarkTint is { } t ? OnTaskbar(t, lightTaskbar) : ink;
                DrawMark(dc, tint, r.MarkTint is null ? 1 : r.MarkAlpha, size);
            }
            // Signal red belongs to the one state a click actually fixes
            if (r.NeedsConnect) Dot(dc, RL.BrandTone.Signal, ground, size, top: false);
            if (r.Waiting && r.Lower is null) Dot(dc, OnTaskbar(RL.State.Warning.Dark, lightTaskbar), ground, size, top: true);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    // 5x6 digits drawn on the pixel grid: at 16 to 20 px two rows of antialiased text blur into
    // each other, and whole pixels do not
    static readonly string[][] Glyphs =
    {
        new[] { ".###.", "#...#", "#...#", "#...#", "#...#", ".###." },
        new[] { "..#..", ".##..", "..#..", "..#..", "..#..", ".###." },
        new[] { ".###.", "#...#", "...#.", "..#..", ".#...", "#####" },
        new[] { "####.", "....#", ".###.", "....#", "....#", "####." },
        new[] { "#..#.", "#..#.", "#..#.", "#####", "...#.", "...#." },
        new[] { "#####", "#....", "####.", "....#", "....#", "####." },
        new[] { ".###.", "#....", "####.", "#...#", "#...#", ".###." },
        new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#..." },
        new[] { ".###.", "#...#", ".###.", "#...#", "#...#", ".###." },
        new[] { ".###.", "#...#", "#...#", ".####", "....#", ".###." },
    };
    const int GlyphRows = 6, GlyphCols = 5;

    static void DrawPixelPair(DrawingContext dc, string top, string bottom, Color topInk, Color bottomInk,
                              Color ruleInk, double ruleAlpha, int size)
    {
        // Two rows and a 1 px rule; the air goes either side of the rule first, then the edges
        var spare = size - (2 * GlyphRows + 1);
        var gap = Math.Clamp(spare / 2, 1, 3);
        var margin = (spare - 2 * gap + 1) / 2;
        var ruleY = margin + GlyphRows + gap;
        PixelDigits(dc, top, topInk, size, margin);
        PixelDigits(dc, bottom, bottomInk, size, ruleY + 1 + gap);
        // A short rule separates without reading as a bar between two gauges
        var ruleW = (int)Math.Round(size * 0.5);
        dc.DrawRectangle(new SolidColorBrush(ruleInk) { Opacity = ruleAlpha }, null, new Rect((size - ruleW) / 2, ruleY, ruleW, 1));
    }

    static void PixelDigits(DrawingContext dc, string digits, Color ink, int size, int y)
    {
        var text = digits.Where(char.IsAsciiDigit).ToArray();
        if (text.Length == 0) return;
        // Three digits (100) lose the inter-digit gap so they still fit 16 px
        var gap = text.Length >= 3 && size < 18 ? 0 : 1;
        var width = text.Length * GlyphCols + (text.Length - 1) * gap;
        var x = (size - width + 1) / 2;
        var brush = new SolidColorBrush(ink);
        foreach (var c in text)
        {
            var g = Glyphs[c - '0'];
            for (var row = 0; row < GlyphRows; row++)
                for (var col = 0; col < GlyphCols; col++)
                    if (g[row][col] == '#') dc.DrawRectangle(brush, null, new Rect(x + col, y + row, 1, 1));
            x += GlyphCols + gap;
        }
    }
    /// <summary>Digits with a small raised suffix ("%", or S and W for split icons): the digits keep the room.</summary>
    static Geometry WithPercent(string digits, Typeface face, double em, Brush brush, string suffix = "%")
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var d = new FormattedText(digits, culture, FlowDirection.LeftToRight, face, em, brush, 1.0).BuildGeometry(new Point(0, 0));
        // The unit is set at normal width: condensed strokes break up at the 6 px a suffix gets
        var unitFace = new Typeface(face.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var p = new FormattedText(suffix, culture, FlowDirection.LeftToRight, unitFace, em * 0.5, brush, 1.0).BuildGeometry(new Point(0, 0));
        var db = d.Bounds;
        var pb = p.Bounds;
        // Tucked against the last digit and aligned to the digits' top
        p.Transform = new TranslateTransform(db.Right + em * 0.06 - pb.X, db.Top - pb.Top);
        var group = new GeometryGroup();
        group.Children.Add(d);
        group.Children.Add(p);
        return group;
    }

    /// <summary>The largest bold digits that fit a full-width band `height` tall starting at `top`.</summary>
    static void DrawNumber(DrawingContext dc, string text, Color color, int size, double top, double height, bool percent,
                           string suffix = "%", string? fitAs = null, Color? unitColor = null)
    {
        // Bahnschrift Condensed ships with Windows: its digits are near square, so a square icon holds
        // them far taller than Segoe's. Segoe UI stays as the fallback
        var face = new Typeface(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed);
        var brush = new SolidColorBrush(color);
        double em = height * (text.Length >= 3 ? 1.1 : 1.6);
        Geometry geo;
        while (true)
        {
            // Sized by `fitAs` when given, so icons shown side by side share one type size
            var probe = percent && fitAs is not null ? WithPercent(fitAs, face, em, brush, "W") : null;
            geo = percent ? WithPercent(text, face, em, brush, suffix)
                : new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                    face, em, brush, 1.0).BuildGeometry(new Point(0, 0));
            var bounds = probe?.Bounds ?? geo.Bounds;
            // Edge to edge: the digits are the whole icon, so any margin is size given away
            if ((bounds.Width <= size && bounds.Height <= height) || em < 4) break;
            em -= 0.5;
        }
        var b = geo.Bounds;
        var dx = (size - b.Width) / 2 - b.X;
        var dy = top + (height - b.Height) / 2 - b.Y;
        // Digits in the status colour, the unit in plain ink, so the letter reads as a label
        dc.PushTransform(new TranslateTransform(Math.Round(dx), Math.Round(dy)));
        if (geo is GeometryGroup g && g.Children.Count == 2)
        {
            dc.DrawGeometry(brush, null, g.Children[0]);
            dc.DrawGeometry(new SolidColorBrush(unitColor ?? color), null, g.Children[1]);
        }
        else dc.DrawGeometry(brush, null, geo);
        dc.Pop();
    }

    /// <summary>The RedLine symbol in one ink, as the template image draws it in the menu bar.</summary>
    static void DrawMark(DrawingContext dc, Color tint, double alpha, int size)
    {
        double s = size;
        Point P(double x, double y) => new(x / 256 * s, y / 256 * s);
        var brush = new SolidColorBrush(tint) { Opacity = alpha };
        var pen = new Pen(brush, Math.Max(1.2, s * 20 / 256)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P(48, 65), false, false);
            c.BezierTo(P(92, 65), P(108, 108), P(139, 125), true, true);
            c.BeginFigure(P(48, 191), false, false);
            c.BezierTo(P(92, 191), P(108, 148), P(139, 131), true, true);
            c.BeginFigure(P(48, 128), false, false);
            c.LineTo(P(214, 128), true, true);
            c.BeginFigure(P(118, 57), false, false);
            c.LineTo(P(151, 57), true, true);
            c.BezierTo(P(187, 57), P(207, 76), P(207, 103), true, true);
            c.BezierTo(P(207, 121), P(198, 131), P(184, 139), true, true);
            c.BeginFigure(P(163, 143), false, false);
            c.LineTo(P(207, 199), true, true);
        }
        dc.DrawGeometry(null, pen, g);
    }

    static void Dot(DrawingContext dc, Color fill, Color ground, int size, bool top)
    {
        var r = Math.Max(2.5, size * 0.17);
        var center = new Point(size - r - 0.5, top ? r + 0.5 : size - r - 0.5);
        dc.DrawEllipse(new SolidColorBrush(ground), null, center, r + 1, r + 1);
        dc.DrawEllipse(new SolidColorBrush(fill), null, center, r, r);
    }

    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
}
