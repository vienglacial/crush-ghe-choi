"""Vẽ avatar nữ chibi kiểu pixel cho hình minh họa (tự vẽ từng điểm ảnh, không phải ảnh người thật).

    python -P design/make_chibi.py design/chibi.png
"""
import sys

from PIL import Image, ImageDraw

W, H, SCALE = 40, 42, 12
OUTLINE = (43, 26, 46, 255)
HAIR, HAIR_LIGHT, HAIR_DARK = (123, 74, 58, 255), (171, 113, 86, 255), (96, 56, 46, 255)
SKIN, SKIN_SHADE = (255, 225, 205, 255), (243, 190, 166, 255)
EYE, IRIS, WHITE = (46, 26, 40, 255), (176, 84, 108, 255), (255, 255, 255, 255)
BLUSH, MOUTH = (255, 150, 170, 255), (214, 66, 104, 255)
DRESS, DRESS_SHADE, COLLAR = (255, 111, 165, 255), (222, 78, 138, 255), (255, 246, 249, 255)
BOW, BOW_DARK = (255, 79, 139, 255), (214, 45, 106, 255)

im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
px = im.load()


def dots(color, *points):
    for x, y in points:
        px[x, y] = color


def run(color, y, x0, x1):
    for x in range(x0, x1 + 1):
        px[x, y] = color


# Tóc phía sau và hai lọn dài hai bên vai
d.ellipse([5, 2, 34, 31], fill=HAIR)
d.rectangle([5, 16, 11, 37], fill=HAIR)
d.rectangle([28, 16, 34, 37], fill=HAIR)
for x, y in [(5, 36), (5, 37), (6, 37), (11, 37), (28, 37), (33, 37), (34, 36), (34, 37)]:
    px[x, y] = (0, 0, 0, 0)
for y in range(20, 36):  # bóng mép trong của lọn tóc
    px[11, y] = HAIR_DARK
    px[28, y] = HAIR_DARK

# Váy và cổ
d.polygon([(12, 41), (13, 32), (16, 30), (23, 30), (26, 32), (27, 41)], fill=DRESS)
for y in range(33, 42):
    px[13, y] = DRESS_SHADE
    px[26, y] = DRESS_SHADE
d.rectangle([18, 27, 21, 31], fill=SKIN_SHADE)
run(COLLAR, 31, 15, 17)
run(COLLAR, 31, 22, 24)
run(COLLAR, 32, 16, 18)
run(COLLAR, 32, 21, 23)
dots(SKIN, (19, 32), (20, 32))
dots(BOW, (17, 34), (18, 34), (20, 34), (21, 34), (17, 35), (18, 35), (19, 35), (20, 35), (21, 35), (18, 36), (19, 36), (20, 36), (19, 37))

# Mặt
d.ellipse([9, 9, 30, 28], fill=SKIN)

# Tóc mái: phủ phần trên mặt, mép dưới lượn răng cưa
for y in range(9, 14):
    for x in range(9, 31):
        if px[x, y] == SKIN:
            px[x, y] = HAIR
for x in list(range(9, 14)) + [17, 18, 22, 23] + list(range(27, 31)):
    if px[x, 14] == SKIN:
        px[x, 14] = HAIR
for x in [9, 10, 11, 28, 29, 30]:
    if px[x, 15] == SKIN:
        px[x, 15] = HAIR
for y in range(16, 22):
    for x in (9, 30):
        if px[x, y] == SKIN:
            px[x, y] = HAIR
dots(HAIR_DARK, (12, 14), (13, 14), (17, 14), (18, 14), (22, 14), (23, 14), (27, 14), (11, 15), (28, 15))

# Vệt sáng trên tóc
run(HAIR_LIGHT, 6, 17, 24)
dots(HAIR_LIGHT, (13, 7), (14, 7), (24, 7), (25, 7), (26, 8))

# Mắt trái mở to, mắt phải nháy
for y in range(17, 20):
    run(EYE, y, 13, 15)
run(IRIS, 20, 13, 15)
run(IRIS, 21, 13, 15)
dots(WHITE, (13, 18))
dots(EYE, (12, 17), (23, 20), (24, 19), (25, 19), (26, 19), (27, 20))

# Má hồng và môi chu hun gió
run(BLUSH, 23, 11, 13)
run(BLUSH, 23, 26, 28)
dots(MOUTH, (19, 24), (20, 24), (19, 25), (20, 25))

# Nơ cài tóc
for x, ys in {6: range(6, 10), 7: range(5, 11), 8: range(5, 11), 9: range(6, 10), 12: range(6, 10), 13: range(5, 11), 14: range(5, 11), 15: range(6, 10)}.items():
    for y in ys:
        px[x, y] = BOW
dots(BOW_DARK, (10, 7), (11, 7), (10, 8), (11, 8), (9, 7), (9, 8), (12, 7), (12, 8))

# Viền ngoài tự động (đáy để phẳng vì là nửa người)
solid = [[px[x, y][3] > 0 for x in range(W)] for y in range(H)]
for y in range(H):
    for x in range(W):
        if solid[y][x]:
            continue
        if any(0 <= x + dx < W and 0 <= y + dy < H and solid[y + dy][x + dx] for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
            px[x, y] = OUTLINE

out = sys.argv[1]
im.resize((W * SCALE, H * SCALE), Image.Resampling.NEAREST).save(out)
print("saved", out, (W * SCALE, H * SCALE))
