# Generates src/Assets/AgosClip.ico (PNG-in-ICO, 16..256) with pure Python.
# Design: "caret A" - a proofreading insert caret (^) forms the A; its crossbar is
# a freshly pasted chip, with a translucent copy offset behind it (large sizes only).
import struct, zlib, math, sys

BG = (21, 22, 28)          # #15161C
FG = (255, 255, 255)
ACCENT = (61, 90, 254)     # #3D5AFE

def rrect_sdf(px, py, cx, cy, hw, hh, r):
    qx = abs(px - cx) - (hw - r); qy = abs(py - cy) - (hh - r)
    ox, oy = max(qx, 0), max(qy, 0)
    return math.hypot(ox, oy) + min(max(qx, qy), 0) - r

def seg_dist(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - ax - t * dx, py - ay - t * dy)

def cov(d, aa):
    return max(0.0, min(1.0, 0.5 - d / aa))

def variant(n):
    # (tile inset, tile radius, mark size, stroke, bar (x,y,w,h) in the 120 box, ghost?)  all in 256-space
    if n >= 48: return 8, 52, 163, 14, (34, 64, 52, 13), True
    if n >= 24: return 0, 56, 192, 15, (34, 63, 52, 15), False
    return 0, 60, 208, 18, (30, 60, 60, 18), False

def render(n):
    s = n / 256.0; aa = max(1.0, 1.2 * s) / s
    inset, rad, msize, stroke, bar, ghost = variant(n)
    k = msize / 120.0; o = 128 - msize / 2
    P = lambda x, y: (o + x * k, o + y * k)
    a1, apex, a2 = P(26, 94), P(60, 24), P(94, 94)
    hw = stroke * k / 2
    bx, by, bw, bh = bar
    bcx, bcy = P(bx + bw / 2, by + bh / 2)
    gcx, gcy = P(bx + bw / 2 + 12, by + bh / 2 - 10)
    bhw, bhh, br = bw * k / 2, bh * k / 2, bh * k / 2
    half = 128 - inset
    rows = []
    for y in range(n):
        row = bytearray([0])
        for x in range(n):
            px, py = (x + 0.5) / s, (y + 0.5) / s
            r = g = b = 0.0; a = 0.0
            def over(c, ca):
                nonlocal r, g, b, a
                na = ca + a * (1 - ca)
                if na <= 0: return
                r = (c[0] * ca + r * a * (1 - ca)) / na
                g = (c[1] * ca + g * a * (1 - ca)) / na
                b = (c[2] * ca + b * a * (1 - ca)) / na
                a = na
            tile = cov(rrect_sdf(px, py, 128, 128, half, half, rad), aa)
            over(BG, tile)
            if ghost:
                over(ACCENT, 0.5 * tile * cov(rrect_sdf(px, py, gcx, gcy, bhw * 48 / 52, bhh, br), aa))
            d = min(seg_dist(px, py, *a1, *apex), seg_dist(px, py, *apex, *a2)) - hw
            over(FG, tile * cov(d, aa))
            over(ACCENT, tile * cov(rrect_sdf(px, py, bcx, bcy, bhw, bhh, br), aa))
            row += bytes((int(round(r)), int(round(g)), int(round(b)), int(round(a * 255))))
        rows.append(bytes(row))
    raw = b"".join(rows)
    def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", n, n, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")

sizes = [16, 20, 24, 32, 40, 48, 64, 256]
pngs = [render(n) for n in sizes]
if len(sys.argv) > 2:  # optional: dump PNGs for preview
    for n, p in zip(sizes, pngs): open(f"{sys.argv[2]}/icon_{n}.png", "wb").write(p)
hdr = struct.pack("<HHH", 0, 1, len(sizes)); off = 6 + 16 * len(sizes); ents = b""
for n, p in zip(sizes, pngs):
    ents += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(p), off); off += len(p)
open(sys.argv[1], "wb").write(hdr + ents + b"".join(pngs))
