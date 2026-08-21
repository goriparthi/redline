#!/bin/bash
# Packs the app icon PNGs into Resources/RedLine.ico for the Windows build.
# Committed output, regenerable input: run this after changing brand/AppIcon.appiconset.
set -euo pipefail
cd "$(dirname "$0")/.."

SRC="brand/AppIcon.appiconset"
OUT="Resources/RedLine.ico"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Windows wants 16, 32, 48 and 256. Each comes from the nearest hand-drawn source rather than
# from one master, so the small sizes keep whatever was done to make them legible.
#
# Every one goes through sips even when the size already matches: the sources are palette PNGs
# and the packer below wants straight RGBA, and a resize is what re-encodes them.
cp "$SRC/icon_16x16.png"   "$WORK/16.png"
cp "$SRC/icon_32x32.png"   "$WORK/32.png"
cp "$SRC/icon_256x256.png" "$WORK/256.png"
sips -z 48 48 "$SRC/icon_256x256.png" --out "$WORK/48.png" >/dev/null

python3 - "$WORK" "$OUT" <<'PY'
import struct, sys, zlib, pathlib

# The small frames are written as BMP rather than PNG on purpose. A PNG-compressed frame is
# legal in an .ico and Explorer reads it, but System.Drawing.Icon, which is what the tray
# ultimately needs an HICON from, rejects it. The 256 stays PNG because that is the one size
# where a BMP frame is 256 KB and nothing asks for it as an HICON.
#
# The sources are a mix of palette and truecolour PNGs, so both are decoded here rather than
# normalised first: sips only re-encodes on an actual resize, and resizing 16 to 16 does
# nothing at all.
def read_png(path):
    data = path.read_bytes()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", f"{path} is not a PNG"
    pos, idat, width, height = 8, b"", 0, 0
    colour, palette, transparency = 6, b"", b""
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            width, height, depth, colour = struct.unpack(">IIBB", body[:10])
            assert depth == 8 and colour in (3, 6), \
                f"{path}: only 8-bit palette or RGBA handled, got {depth}/{colour}"
        elif kind == b"PLTE":
            palette = body
        elif kind == b"tRNS":
            transparency = body
        elif kind == b"IDAT":
            idat += body
        elif kind == b"IEND":
            break
        pos += 12 + length

    raw = zlib.decompress(idat)
    channels = 1 if colour == 3 else 4
    stride = width * channels
    out, previous, at = [], bytearray(stride), 0
    for _ in range(height):
        filt = raw[at]; at += 1
        line = bytearray(raw[at:at + stride]); at += stride
        for i in range(stride):
            a = line[i - channels] if i >= channels else 0
            b = previous[i]
            c = previous[i - channels] if i >= channels else 0
            if filt == 1:   line[i] = (line[i] + a) & 0xFF
            elif filt == 2: line[i] = (line[i] + b) & 0xFF
            elif filt == 3: line[i] = (line[i] + (a + b) // 2) & 0xFF
            elif filt == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 0xFF
        previous = bytearray(line)
        if colour == 3:
            # Index into the palette, with tRNS supplying alpha for the entries that have it
            expanded = bytearray()
            for index in line:
                expanded += palette[index * 3:index * 3 + 3]
                expanded.append(transparency[index] if index < len(transparency) else 255)
            out.append(bytes(expanded))
        else:
            out.append(bytes(line))
    return width, height, out


def bmp_frame(width, height, rows):
    header = struct.pack("<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0,
                         width * height * 4, 0, 0, 0, 0)
    # BGRA, bottom-up, then an all-zero AND mask: the alpha channel already carries the shape
    pixels = b""
    for row in reversed(rows):
        pixels += bytes(b for i in range(0, len(row), 4)
                        for b in (row[i + 2], row[i + 1], row[i], row[i + 3]))
    mask = b"\x00" * (((width + 31) // 32) * 4 * height)
    return header + pixels + mask


work, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
frames = []
for size in (16, 32, 48):
    w, h, rows = read_png(work / f"{size}.png")
    frames.append((size, bmp_frame(w, h, rows)))
frames.append((256, (work / "256.png").read_bytes()))

header = struct.pack("<HHH", 0, 1, len(frames))
offset = len(header) + 16 * len(frames)
entries, payload = b"", b""
for size, blob in frames:
    dimension = 0 if size >= 256 else size      # 0 means 256 in this format
    entries += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(blob), offset)
    payload += blob
    offset += len(blob)

out.write_bytes(header + entries + payload)
print(f"wrote {out} ({out.stat().st_size} bytes, {len(frames)} sizes)")
PY
