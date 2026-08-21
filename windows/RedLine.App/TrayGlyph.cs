using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace RedLine.App;

/// <summary>
/// The reading, drawn into the tray icon.
///
/// Windows has no menu-bar text to put a percentage in the way macOS does, and taskbar
/// toolbars were removed in Windows 11, so the icon itself has to carry the number. This is
/// what the battery and network meters have always done.
/// </summary>
internal static class TrayGlyph
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// Renders <paramref name="text"/> in <paramref name="colour"/> at icon size.
    ///
    /// The caller owns the result and must pass it to <see cref="Release"/>: GetHicon hands
    /// back a GDI handle that the Icon wrapper does not free, and a tray icon redrawn every
    /// minute would leak one every time.
    /// </summary>
    public static Icon Render(string text, Color colour, int size = 32)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        try
        {
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                // Shrink to fit rather than clip: "100" has to stay readable at 16 px
                var fontSize = text.Length >= 3 ? size * 0.46f : size * 0.62f;
                using var font = new Font("Segoe UI", fontSize, FontStyle.Bold,
                                          GraphicsUnit.Pixel);
                using var brush = new SolidBrush(colour);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), format);
            }

            var handle = bitmap.GetHicon();
            try
            {
                // Cloned so the Icon owns managed memory rather than the raw handle, which is
                // then destroyed immediately instead of at some unknowable later point
                using var borrowed = Icon.FromHandle(handle);
                return (Icon)borrowed.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    public static void Release(Icon? icon) => icon?.Dispose();
}
