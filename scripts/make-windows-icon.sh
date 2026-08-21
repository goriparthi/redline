#!/bin/bash
# Packs the app icon PNGs into Resources/RedLine.ico for the Windows build.
# Committed output, regenerable input: run this after changing brand/AppIcon.appiconset.
set -euo pipefail
cd "$(dirname "$0")/.."

SRC="brand/AppIcon.appiconset"
OUT="Resources/RedLine.ico"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Windows wants 16, 32, 48 and 256. The set has every size but 48, so that one is resampled
# from the 256 rather than from a smaller source.
cp "$SRC/icon_16x16.png" "$WORK/16.png"
cp "$SRC/icon_32x32.png" "$WORK/32.png"
cp "$SRC/icon_256x256.png" "$WORK/256.png"
sips -z 48 48 "$SRC/icon_256x256.png" --out "$WORK/48.png" >/dev/null

python3 - "$WORK" "$OUT" <<'PY'
import struct, sys, pathlib

work, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
sizes = [16, 32, 48, 256]
blobs = [(s, (work / f"{s}.png").read_bytes()) for s in sizes]

# ICONDIR, then one ICONDIRENTRY per image, then the PNGs. PNG inside ICO is understood by
# every Windows this app targets, so there is no need to also emit a BMP form.
header = struct.pack("<HHH", 0, 1, len(blobs))
offset = len(header) + 16 * len(blobs)
entries, payload = b"", b""
for size, data in blobs:
    dimension = 0 if size >= 256 else size      # 0 means 256 in this format
    entries += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32,
                           len(data), offset)
    payload += data
    offset += len(data)

out.write_bytes(header + entries + payload)
print(f"wrote {out} ({out.stat().st_size} bytes, {len(blobs)} sizes)")
PY
