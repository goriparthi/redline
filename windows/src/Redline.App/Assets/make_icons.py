# Regenerates RedLine.ico and the tray icons from brand/. Run: python make_icons.py (needs Pillow).
# The tray path is brand/menu-bar/RedlineTemplate.svg, traced by hand because Pillow has no SVG.
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
BRAND = os.path.normpath(os.path.join(HERE, "..", "..", "..", "..", "brand"))

def app_icon():
    src = Image.open(os.path.join(BRAND, "masters", "redline-app-icon-1024.png")).convert("RGBA")
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [src.resize((s, s), Image.LANCZOS) for s in sizes]
    frames[-1].save(os.path.join(HERE, "RedLine.ico"), format="ICO",
                    sizes=[(s, s) for s in sizes], append_images=frames[:-1])

def bez(p0, p1, p2, p3, n=40):
    out = []
    for i in range(n + 1):
        t = i / n
        a, b, c, d = (1 - t) ** 3, 3 * (1 - t) ** 2 * t, 3 * (1 - t) * t * t, t ** 3
        out.append((a * p0[0] + b * p1[0] + c * p2[0] + d * p3[0],
                    a * p0[1] + b * p1[1] + c * p2[1] + d * p3[1]))
    return out

# RedlineTemplate.svg, viewBox 0 0 18 18, stroke 1.7, round caps and joins
STROKES = [
    bez((2.25, 3.25), (5.4, 3.25), (6.35, 7.1), (8.75, 8.75)),
    bez((2.25, 14.75), (5.4, 14.75), (6.35, 10.9), (8.75, 9.25)),
    [(2.25, 9), (14.5, 9)],
    [(8.2, 2.6), (10.65, 2.6)] + bez((10.65, 2.6), (13.65, 2.6), (15.4, 4.25), (15.4, 6.6))[1:]
    + bez((15.4, 6.6), (15.4, 8.15), (14.6, 9.05), (13.35, 9.7))[1:] + [(15.6, 15.4)],
    [(9.1, 9), (16, 9)],
]

def tray(size, rgb):
    ss = 16
    big = size * ss
    im = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    k = big / 18
    w = 1.7 * k
    for pts in STROKES:
        sp = [(x * k, y * k) for x, y in pts]
        d.line(sp, fill=rgb + (255,), width=int(round(w)), joint="curve")
        for x, y in sp:
            d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=rgb + (255,))
    return im.resize((size, size), Image.LANCZOS)

def tray_icons():
    sizes = [16, 20, 24, 32]
    for name, rgb in (("TrayWhite", (255, 255, 255)), ("TrayDark", (11, 13, 16))):
        frames = [tray(s, rgb) for s in sizes]
        frames[-1].save(os.path.join(HERE, name + ".ico"), format="ICO",
                        sizes=[(s, s) for s in sizes], append_images=frames[:-1])

if __name__ == "__main__":
    app_icon()
    tray_icons()
