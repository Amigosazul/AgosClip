# Generates src/Assets/AgosClip.ico (PNG-in-ICO, 16..256) with pure Python.
import struct, zlib, math, sys

def rrect_sdf(px, py, cx, cy, hw, hh, r):
    qx = abs(px - cx) - (hw - r); qy = abs(py - cy) - (hh - r)
    ox, oy = max(qx, 0), max(qy, 0)
    return math.hypot(ox, oy) + min(max(qx, qy), 0) - r

def cov(d, aa):  # coverage from signed distance
    return max(0.0, min(1.0, 0.5 - d / aa))

def render(n):
    s = n / 256.0; aa = max(1.0, 1.2 * s) / s  # work in 256-space
    rows = []
    for y in range(n):
        row = bytearray([0])
        for x in range(n):
            px, py = (x + 0.5) / s, (y + 0.5) / s
            # board
            board = rrect_sdf(px, py, 128, 140, 88, 104, 26)
            a_board = cov(board, aa)
            # paper inset
            paper = rrect_sdf(px, py, 128, 150, 64, 78, 12)
            a_paper = cov(paper, aa)
            # clip at top
            clip = rrect_sdf(px, py, 128, 44, 40, 22, 14)
            a_clip = cov(clip, aa)
            # text lines on paper
            a_line = 0.0
            for ly, lw in ((112, 40), (146, 40), (180, 26)):
                a_line = max(a_line, cov(rrect_sdf(px, py, 128 - (40 - lw), ly, lw, 7, 7), aa))
            # composite: board gradient blue -> violet
            t = py / 256
            br, bg, bb = (int(40 + 60 * t), int(110 - 40 * t), int(235 - 10 * t))
            r = g = b = 0; a = 0.0
            def over(cr, cg, cb, ca):
                nonlocal r, g, b, a
                na = ca + a * (1 - ca)
                if na <= 0: return
                r = (cr * ca + r * a * (1 - ca)) / na
                g = (cg * ca + g * a * (1 - ca)) / na
                b = (cb * ca + b * a * (1 - ca)) / na
                a = na
            over(br, bg, bb, a_board)
            over(250, 250, 252, a_paper)
            over(br, bg, bb, a_line * a_paper)
            over(55, 60, 80, a_clip)
            over(230, 232, 240, cov(rrect_sdf(px, py, 128, 44, 18, 6, 6), aa) * a_clip)
            row += bytes((int(r), int(g), int(b), int(round(a * 255))))
        rows.append(bytes(row))
    raw = b"".join(rows)
    def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", n, n, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")

sizes = [16, 20, 24, 32, 40, 48, 64, 256]
pngs = [render(n) for n in sizes]
hdr = struct.pack("<HHH", 0, 1, len(sizes)); off = 6 + 16 * len(sizes); ents = b""
for n, p in zip(sizes, pngs):
    ents += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(p), off); off += len(p)
open(sys.argv[1], "wb").write(hdr + ents + b"".join(pngs))
