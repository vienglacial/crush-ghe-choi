"""Tạo icon trái tim (app.ico), tiếng chuông nhẹ (chime.wav) và hình mẫu của bộ có sẵn cho app.

    python -P tools/make_app_assets.py src/CrushGheChoi/Assets
"""

from __future__ import annotations

import json
import math
import struct
import sys
import wave
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

HEART = [  # đường viền trái tim trong hộp 24×24 (giống hình tim app vẽ)
    (12, 21.35), (10.55, 20.03), (5.4, 15.36), (2, 12.28), (2, 8.5), (2, 5.42), (4.42, 3), (7.5, 3),
    (9.24, 3), (10.91, 3.81), (12, 5.09), (13.09, 3.81), (14.76, 3), (16.5, 3), (19.58, 3), (22, 5.42),
    (22, 8.5), (22, 12.28), (18.6, 15.36), (13.45, 20.04),
]


def bezier_heart(size: int) -> list[tuple[float, float]]:
    """Lấy mẫu đường cong tim từ các nhóm điểm Bezier bậc ba ở trên."""
    p = HEART
    segs = [(p[1], p[2], p[3], p[4]), (p[4], p[5], p[6], p[7]), (p[7], p[8], p[9], p[10]),
            (p[10], p[11], p[12], p[13]), (p[13], p[14], p[15], p[16]), (p[16], p[17], p[18], p[19])]
    pts = [p[0]]
    for a, b, c, d in segs:
        for i in range(1, 25):
            t = i / 24
            mt = 1 - t
            x = mt**3 * a[0] + 3 * mt * mt * t * b[0] + 3 * mt * t * t * c[0] + t**3 * d[0]
            y = mt**3 * a[1] + 3 * mt * mt * t * b[1] + 3 * mt * t * t * c[1] + t**3 * d[1]
            pts.append((x, y))
    k = size / 24
    return [(x * k, (y + 0.4) * k) for x, y in pts]


def heart_icon(size: int) -> Image.Image:
    ss = 4
    big = size * ss
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).polygon(bezier_heart(big), fill=255)
    # Gradient từ hồng sáng (trên trái) sang đỏ hồng (dưới phải).
    grad = Image.new("RGBA", (big, big))
    gp = grad.load()
    for y in range(big):
        for x in range(big):
            t = min(1.0, (x * 0.4 + y * 0.9) / (big * 1.1))
            gp[x, y] = (int(255 - 20 * t), int(120 - 75 * t), int(170 - 85 * t), 255)
    img.paste(grad, (0, 0), mask)
    shine = Image.new("L", (big, big), 0)
    ImageDraw.Draw(shine).ellipse((big * 0.2, big * 0.2, big * 0.42, big * 0.36), fill=150)
    shine = shine.filter(ImageFilter.GaussianBlur(big * 0.03))
    white = Image.new("RGBA", (big, big), (255, 255, 255, 255))
    img = Image.composite(white, img, Image.eval(shine, lambda v: v)).copy()
    img.putalpha(mask)
    return img.resize((size, size), Image.Resampling.LANCZOS)


def mascot(out_dir: Path) -> None:
    """Hình mẫu khi người dùng chưa thêm ảnh crush: một trái tim đang hun gió (không phải ảnh người thật)."""
    size, ss = 560, 4
    big = size * ss
    body = heart_icon(size).resize((big, big), Image.Resampling.BICUBIC)  # cùng màu và vệt sáng với icon app
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    img.alpha_composite(body)
    ink = (61, 36, 51, 255)
    d = ImageDraw.Draw(img)
    w = round(big * 0.022)
    # Mắt nhắm cười (hai vòng cung) và má hồng.
    for cx in (0.37, 0.61):
        d.arc((big * (cx - 0.065), big * 0.36, big * (cx + 0.065), big * 0.47), 200, 340, fill=ink, width=w)
    blush = Image.new("L", (big, big), 0)
    bd = ImageDraw.Draw(blush)
    for cx in (0.31, 0.68):
        bd.ellipse((big * (cx - 0.06), big * 0.47, big * (cx + 0.06), big * 0.53), fill=190)
    blush = blush.filter(ImageFilter.GaussianBlur(big * 0.012))
    img.paste(Image.new("RGBA", (big, big), (255, 170, 196, 255)), (0, 0), blush)
    # Môi chu ra hun gió, lệch sang phải; tim bay ra từ đây.
    lips = (196, 30, 92, 255)
    mx, my, r = big * 0.535, big * 0.555, big * 0.032
    d.ellipse((mx - r, my - r * 1.15, mx + r, my + r * 1.15), fill=lips)
    d.ellipse((mx - r * 0.45, my - r * 0.5, mx + r * 0.45, my + r * 0.5), fill=(120, 16, 60, 255))
    img = img.resize((size, size), Image.Resampling.LANCZOS)
    box = img.getbbox()
    img = img.crop(box)
    out_dir.mkdir(parents=True, exist_ok=True)
    img.save(out_dir / "still.png", optimize=True)
    kiss = {"x": round((mx / ss - box[0]) / img.width + 0.06, 3), "y": round((my / ss - box[1]) / img.height, 3)}
    manifest = {
        "schema": 1,
        "name": "Hình mẫu",
        "version": "2.0.0",
        "still": "still.png",
        "fps": 24,
        "kissPoint": kiss,
        "stills": [{"file": "still.png", "kissPoint": kiss, "drift": 1}],
        "clips": {},
    }
    text = json.dumps(manifest, ensure_ascii=False, indent=2)
    (out_dir / "manifest.json").write_text(text + chr(10), encoding="utf-8")
    print("mascot", img.size, kiss)


def chime(path: Path) -> None:
    rate = 44100
    notes = [(1318.5, 0.0), (1760.0, 0.11)]  # E6 rồi A6, nhỏ và ngắn
    length = 0.9
    frames = bytearray()
    for i in range(int(rate * length)):
        t = i / rate
        v = 0.0
        for freq, start in notes:
            if t >= start:
                u = t - start
                env = min(1.0, u / 0.006) * math.exp(-u * 6.5)
                v += env * (math.sin(2 * math.pi * freq * u) + 0.25 * math.sin(4 * math.pi * freq * u))
        frames += struct.pack("<h", int(max(-1, min(1, v * 0.16)) * 32767))
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(bytes(frames))


def main() -> None:
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    icons = [heart_icon(s) for s in sizes]
    icons[-1].save(out / "app.ico", sizes=[(s, s) for s in sizes], append_images=icons[:-1])
    icons[-1].save(out / "heart-256.png")
    chime(out / "chime.wav")
    mascot(out / "Crush" / "default")
    print("ok")


if __name__ == "__main__":
    main()
