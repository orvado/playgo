#!/usr/bin/env python3
"""Generates src/PlayGo.App/playgo.ico, the application icon.

The icon is drawn procedurally rather than committed as an opaque binary so it
can be retuned by editing numbers here. It mirrors the colours the board
renderer actually uses: wooden board, dark grid, hoshi points, and two stones.

Run from the repository root:

    python tools/make-icon.py

Anti-aliasing comes from supersampling: each output pixel is the average of a
grid of point samples, so no external imaging library is needed.
"""

import math
import struct
import zlib
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "src" / "PlayGo.App" / "playgo.ico"
SIZES = (256, 128, 64, 48, 32, 16)

# Palette, matching GoBoardControl.
WOOD_A = (0xE9, 0xC8, 0x8A)
WOOD_B = (0xC8, 0x9B, 0x55)
BORDER = (0x6B, 0x4E, 0x23)
GRID = (0x33, 0x23, 0x0E)
STAR = (0x2A, 0x1B, 0x09)
BLACK_HI, BLACK_LO = (0x74, 0x74, 0x78), (0x0A, 0x0A, 0x0C)
WHITE_HI, WHITE_LO = (0xFF, 0xFF, 0xFF), (0xBE, 0xBE, 0xBE)

BOARD_INSET = 0.035
BOARD_RADIUS = 0.16
GRID_N = 5               # lines per side; coarse enough to read at 16 px
GRID_SPAN = (0.17, 0.83) # where the grid sits, in canvas fractions
STONES = (((1, 1), True), ((3, 3), False))  # (grid index, is black)


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def clamp(v, lo, hi):
    return lo if v < lo else hi if v > hi else v


def sd_rounded_box(x, y, x0, y0, x1, y1, r):
    """Signed distance to a rounded rectangle; negative inside."""
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    hx, hy = (x1 - x0) / 2 - r, (y1 - y0) / 2 - r
    qx, qy = abs(x - cx) - hx, abs(y - cy) - hy
    return math.hypot(max(qx, 0.0), max(qy, 0.0)) + min(max(qx, qy), 0.0) - r


def stone_colour(x, y, cx, cy, radius, is_black):
    """Radial-shaded stone, lit from the upper left like the in-app stones."""
    # Drop shadow: a soft dark pool just below and to the right.
    sx, sy = cx + 0.10 * radius, cy + 0.14 * radius
    sd = math.hypot(x - sx, y - sy) / radius
    if sd > 1.0 and math.hypot(x - cx, y - cy) > radius:
        shadow = clamp(0.42 * (1.6 - sd), 0.0, 0.42)
        if shadow > 0:
            return (0x28, 0x1A, 0x08) + (round(shadow * 255),)

    d = math.hypot(x - cx, y - cy) / radius
    if d > 1.0:
        return (0, 0, 0, 0)

    hi, lo = (BLACK_HI, BLACK_LO) if is_black else (WHITE_HI, WHITE_LO)
    lx, ly = cx - 0.30 * radius, cy - 0.34 * radius
    t = clamp(math.hypot(x - lx, y - ly) / (1.5 * radius), 0.0, 1.0)
    rgb = lerp(hi, lo, t ** 0.85)

    # Darken the very edge so the stone reads as a solid, rounded object.
    rim = clamp((d - 0.80) / 0.20, 0.0, 1.0)
    rgb = lerp(rgb, lo, rim * 0.55)
    return rgb + (255,)


def sample(x, y, size):
    """Returns (r, g, b, a) for one point in canvas fractions."""
    # Board: rounded square with a diagonal gradient and a darker rim.
    d = sd_rounded_box(x, y, BOARD_INSET, BOARD_INSET,
                       1 - BOARD_INSET, 1 - BOARD_INSET, BOARD_RADIUS)
    if d > 0:
        return (0, 0, 0, 0)
    if d > -max(0.012, 1.6 / size):
        return BORDER + (255,)

    wood_t = clamp((x - BOARD_INSET + (y - BOARD_INSET)) / 1.6, 0.0, 1.0)
    rgb = lerp(WOOD_A, WOOD_B, wood_t)

    # Grid lines.
    g0, g1 = GRID_SPAN
    step = (g1 - g0) / (GRID_N - 1)
    half = clamp(1.05 / size, 0.006, 0.030)
    for i in range(GRID_N):
        p = g0 + i * step
        if abs(x - p) < half or abs(y - p) < half:
            if g0 - step * 0.5 <= x <= g1 + step * 0.5 and g0 - step * 0.5 <= y <= g1 + step * 0.5:
                rgb = GRID
                break

    # Hoshi: the centre point of a 5x5 grid, plus the corners of the grid area.
    else:
        for (hx, hy) in ((2, 2), (0, 0), (0, 4), (4, 0), (4, 4)):
            px, py = g0 + hx * step, g0 + hy * step
            if math.hypot(x - px, y - py) < max(0.9 / size, 0.012):
                rgb = STAR
                break

    # Stones sit on top of the board, so they win over grid and hoshi.
    radius = step * 0.52
    for (gi, gj), is_black in STONES:
        cx, cy = g0 + gi * step, g0 + gj * step
        stone = stone_colour(x, y, cx, cy, radius, is_black)
        if stone[3] > 0:
            if stone[3] == 255:
                return stone
            sr, sg, sb, sa = stone
            a = sa / 255
            return (lerp(rgb, (sr, sg, sb), a) + (255,))
        _ = radius, cx, cy

    return rgb + (255,)


def render(size, supersample):
    rows = []
    total = supersample * supersample
    for py in range(size):
        row = bytearray()
        for px in range(size):
            r = g = b = a = 0
            for sy in range(supersample):
                for sx in range(supersample):
                    fx = (px + (sx + 0.5) / supersample) / size
                    fy = (py + (sy + 0.5) / supersample) / size
                    sr, sg, sb, sa = sample(fx, fy, size)
                    r += sr; g += sg; b += sb; a += sa
            row += bytes((r // total, g // total, b // total, a // total))
        rows.append(bytes(row))
    return rows


def png_bytes(width, height, rows):
    raw = b"".join(b"\x00" + row for row in rows)
    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def main():
    images = []
    for size in SIZES:
        # Small icons get more samples per pixel, where aliasing shows most.
        ss = 4 if size <= 32 else 3 if size <= 64 else 2
        images.append((size, png_bytes(size, size, render(size, ss))))
        print(f"rendered {size}x{size}")

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for size, data in images:
        entries += struct.pack("<BBBBHHII",
                               size if size < 256 else 0,
                               size if size < 256 else 0,
                               0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)

    OUT.write_bytes(header + entries + blobs)
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
