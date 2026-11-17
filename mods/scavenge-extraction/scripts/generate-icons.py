"""Original low-resolution placeholder art, no third-party or native game assets."""
from pathlib import Path
import struct
import zlib

root = Path(__file__).resolve().parent.parent / "Content" / "Textures" / "Items"
root.mkdir(parents=True, exist_ok=True)

def png(name, width, height, draw):
    pixels = bytearray(width * height * 4)
    def rect(x0, y0, x1, y1, color):
        for y in range(max(0, y0), min(height, y1)):
            for x in range(max(0, x0), min(width, x1)):
                pos = (y * width + x) * 4
                pixels[pos:pos + 4] = bytes(color)
    draw(rect)
    raw = b"".join(b"\x00" + pixels[y * width * 4:(y + 1) * width * 4] for y in range(height))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    result = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    result += chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")
    (root / f"{name}.png").write_bytes(result)

def unknown(r):
    r(0, 0, 32, 32, (41, 44, 45, 255))
    r(1, 1, 31, 31, (24, 28, 29, 255))
    c = (207, 213, 210, 255)
    for x, y, w, h in [(5, 3, 6, 2), (4, 4, 2, 2), (9, 4, 2, 4), (7, 7, 3, 2), (7, 9, 2, 2), (7, 12, 2, 2)]:
        r(x * 2, y * 2, (x + w) * 2, (y + h) * 2, c)

def corpse(r):
    c = (94, 103, 106, 255)
    r(11, 1, 21, 11, c)
    r(9, 10, 23, 29, c)
    r(4, 13, 10, 31, c)
    r(23, 13, 29, 31, c)
    r(9, 28, 15, 47, c)
    r(17, 28, 23, 47, c)
    r(13, 4, 15, 6, (27, 30, 31, 255))
    r(18, 4, 20, 6, (27, 30, 31, 255))
    r(12, 15, 20, 18, (124, 77, 68, 255))

def pack(r):
    r(4, 2, 12, 5, (134, 128, 94, 255))
    r(2, 5, 14, 16, (95, 101, 75, 255))
    r(4, 8, 12, 13, (55, 64, 49, 255))

def part(r):
    r(3, 5, 13, 12, (110, 69, 64, 255))
    r(5, 3, 11, 14, (141, 90, 79, 255))
    r(7, 5, 9, 12, (194, 180, 151, 255))

png("unknown", 32, 32, unknown)
png("corpse", 32, 48, corpse)
png("pack", 16, 16, pack)
png("part", 16, 16, part)
print("Generated 4 original item placeholders.")
