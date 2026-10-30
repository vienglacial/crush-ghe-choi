"""Tách nền các ảnh crush và xuất một bộ hình "ảnh đứng" cho app.

Dùng model BiRefNet-portrait hoặc ISNet (định dạng ONNX của rembg) qua onnxruntime; chạy trên CPU, không cần GPU.
BiRefNet-portrait tách người tốt hơn hẳn (ít dính nền, không ăn mất áo trắng hay da), nên dùng nó:
https://github.com/danielgatis/rembg/releases/download/v0.0.0/BiRefNet-portrait-epoch_150.onnx

    python -P tools/prepare_asset.py tools/crush-set.json \
        --model path/to/BiRefNet-portrait-epoch_150.onnx \
        --out src/CrushGheChoi/Assets/Crush/default [--preview preview.jpg] [--only 3]

Mỗi ảnh trong cấu hình (tọa độ tính trên ảnh gốc, pixel):
    source       tệp ảnh gốc (đường dẫn tương đối so với tệp cấu hình)
    out          tên tệp PNG xuất ra trong thư mục bộ hình
    bottom       y cắt ngang để lấy nửa người; bỏ trống thì giữ tới đáy ảnh
    top          y cắt phía trên (hiếm khi cần)
    region       [trái, trên, phải, dưới] quanh người; bỏ trống thì tự tìm
    seed         [x, y] một điểm chắc chắn thuộc người, khi tự tìm bắt nhầm (ví dụ chỉ được cánh tay)
    erase        [[trái, trên, phải, dưới], ...] vùng xóa hẳn, mép mờ (ví dụ bàn tay bị khung ảnh cắt, lọt thỏm ở góc)
    cleanup      0..0.6, càng cao càng xóa mạnh các vệt mờ của nền (mặc định 0.08)
    despillPink  true nếu nền hồng/tím lọt qua kẽ tóc (ví dụ hoa anh đào)
    kissAt       [x, y] trên ảnh gốc: nơi tim bay ra (môi, bàn tay); tự đổi ra tỷ lệ theo khung cắt cuối cùng
    kissPoint    {x, y} tỷ lệ 0..1 trên ảnh xuất ra, nếu không dùng kissAt
    drift        -1 (tim lệch trái) hoặc 1 (lệch phải); bỏ trống thì app tự chọn
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image, ImageDraw, ImageOps

MODEL_SIZE = 1024
MEAN = (0.485, 0.456, 0.406)
IMAGENET_STD = (0.229, 0.224, 0.225)
MAX_OUTPUT_HEIGHT = 960
BLOB_ANALYSIS_SIDE = 480


class Segmenter:
    """Bọc phiên onnxruntime; BiRefNet và ISNet khác nhau ở chuẩn hóa đầu vào và đầu ra (logit hay xác suất)."""

    def __init__(self, model_path: str):
        opts = ort.SessionOptions()
        # BiRefNet cần vài GB cho mỗi lần chạy; arena giữ lại bộ nhớ giữa các lần nên tới ảnh thứ hai là hết RAM.
        opts.enable_cpu_mem_arena = False
        opts.enable_mem_pattern = False
        self.session = ort.InferenceSession(model_path, opts, providers=["CPUExecutionProvider"])
        self.birefnet = "birefnet" in Path(model_path).name.lower()


def predict_mask(seg: Segmenter, image: Image.Image) -> np.ndarray:
    """Alpha thô 0..1 cùng kích thước ảnh vào (giống tiền xử lý của rembg)."""
    resized = image.convert("RGB").resize((MODEL_SIZE, MODEL_SIZE), Image.Resampling.LANCZOS)
    arr = np.asarray(resized, dtype=np.float32)
    arr = arr / max(arr.max(), 1e-6)
    arr = arr - np.array(MEAN, dtype=np.float32)
    if seg.birefnet:
        arr = arr / np.array(IMAGENET_STD, dtype=np.float32)
    tensor = arr.transpose(2, 0, 1)[None].astype(np.float32)
    out = seg.session.run(None, {seg.session.get_inputs()[0].name: tensor})[0][0, 0]
    if seg.birefnet:
        out = 1 / (1 + np.exp(-out))
    out = (out - out.min()) / max(out.max() - out.min(), 1e-6)
    mask = Image.fromarray((out * 255).astype(np.uint8)).resize(image.size, Image.Resampling.LANCZOS)
    return np.asarray(mask, dtype=np.float32) / 255.0


def box_blur(a: np.ndarray, r: int) -> np.ndarray:
    """Trung bình cửa sổ (2r+1)² bằng tổng tích lũy; giữ nguyên kích thước."""
    pad = np.pad(a, ((r + 1, r), (r + 1, r)) + ((0, 0),) * (a.ndim - 2), mode="edge")
    c = pad.cumsum(0).cumsum(1)
    k = 2 * r + 1
    s = c[k:, k:] - c[:-k, k:] - c[k:, :-k] + c[:-k, :-k]
    return s / (k * k)


def guided_filter(guide: np.ndarray, src: np.ndarray, r: int, eps: float) -> np.ndarray:
    """Guided filter một kênh (He et al.) để mép tóc bám theo ảnh gốc."""
    mean_i = box_blur(guide, r)
    mean_p = box_blur(src, r)
    cov = box_blur(guide * src, r) - mean_i * mean_p
    var = box_blur(guide * guide, r) - mean_i * mean_i
    a = cov / (var + eps)
    b = mean_p - a * mean_i
    return box_blur(a, r) * guide + box_blur(b, r)


def flood(solid: np.ndarray, seed: tuple[int, int]) -> np.ndarray:
    keep = np.zeros_like(solid)
    if not solid[seed]:
        return solid.copy()
    keep[seed] = True
    frontier = keep.copy()
    while frontier.any():
        grown = np.zeros_like(frontier)
        grown[1:] |= frontier[:-1]
        grown[:-1] |= frontier[1:]
        grown[:, 1:] |= frontier[:, :-1]
        grown[:, :-1] |= frontier[:, 1:]
        frontier = grown & solid & ~keep
        keep |= frontier
    return keep


def main_blob(alpha: np.ndarray, threshold: float = 0.3, seed_xy: tuple[int, int] | None = None) -> np.ndarray:
    """Vùng liên thông chính (chứa điểm seed, hoặc chỗ dày đặc nhất), tính trên bản thu nhỏ cho nhanh."""
    h, w = alpha.shape
    s = BLOB_ANALYSIS_SIDE / max(h, w)
    small = np.asarray(Image.fromarray((alpha * 255).astype(np.uint8)).resize((max(1, round(w * s)), max(1, round(h * s)))), dtype=np.float32) / 255
    solid = small > threshold
    if seed_xy is not None:
        seed = (min(solid.shape[0] - 1, max(0, round(seed_xy[1] * s))), min(solid.shape[1] - 1, max(0, round(seed_xy[0] * s))))
    else:
        density = box_blur(solid.astype(np.float32), 8)
        seed = np.unravel_index(np.argmax(density * solid), solid.shape)
    keep = flood(solid, seed)
    big = np.asarray(Image.fromarray(keep.astype(np.uint8) * 255).resize((w, h), Image.Resampling.NEAREST)) > 0
    return box_blur(big.astype(np.float32), max(3, round(6 / s / 4))) > 0


def fill_pinholes(alpha: np.ndarray) -> np.ndarray:
    """Lỗ li ti giữa người (mặt, áo) do model đoán sót: chỗ gần như bị phần đặc bao kín thì làm đặc lại.

    Mép ngoài, kẽ tóc và khe giữa các ngón tay không bị bao kín nên vẫn giữ nguyên.
    """
    r = max(3, round(0.004 * max(alpha.shape)))
    enclosed = box_blur((alpha > 0.6).astype(np.float32), r) > 0.85
    return np.where(enclosed, 1.0, alpha)


def erase_boxes(alpha: np.ndarray, boxes: list[tuple[int, int, int, int]]) -> np.ndarray:
    """Xóa các vùng chữ nhật (tọa độ vùng cắt), mép mờ vài pixel để không lộ đường thẳng."""
    mask = np.zeros_like(alpha)
    h, w = alpha.shape
    for l, t, r, b in boxes:
        mask[max(0, t):min(h, b), max(0, l):min(w, r)] = 1
    return alpha * (1 - np.clip(box_blur(mask, 6), 0, 1))


def despill_pink(rgb: np.ndarray, alpha: np.ndarray, reach: int = 28) -> tuple[np.ndarray, np.ndarray]:
    """Hoa nền lọt qua kẽ tóc: gần mép, pixel có cả đỏ và lam vượt lục thì làm trong và bớt sắc hồng.

    Chỉ bật cho ảnh nền hồng/tím: áo đỏ hay môi cũng có thể bị đụng tới nếu bật bừa.
    """
    near_edge = box_blur((alpha < 0.5).astype(np.float32), reach) > 1e-3
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    spill = np.where(near_edge, np.clip(np.minimum(r, b) - g, 0, 1), 0.0)
    pink = np.clip(spill / 0.18, 0, 1)
    out = rgb.copy()
    out[..., 0] -= spill
    out[..., 2] -= spill
    return np.clip(out, 0, 1), alpha * (1 - 0.85 * pink)


def decontaminate(rgb: np.ndarray, alpha: np.ndarray) -> np.ndarray:
    """Thay màu ở mép bán trong suốt bằng màu của phần người kế bên, tránh viền màu của nền cũ."""
    solid = (alpha > 0.92).astype(np.float32)
    fg = rgb.copy()
    filled = solid.copy()
    for r in (1, 2, 4, 8, 16, 32):
        w = box_blur(filled, r)
        c = box_blur(fg * filled[..., None], r)
        est = c / np.maximum(w[..., None], 1e-6)
        need = (filled < 0.5) & (w > 1e-4)
        fg[need] = est[need]
        filled = np.where(need, 1.0, filled)
    return np.where((alpha < 0.92)[..., None], fg, rgb)


def fade_cut_edges(alpha: np.ndarray, touches: dict[str, bool]) -> np.ndarray:
    """Ảnh selfie hay bị khung cắt ngang tay/vai: làm mờ dần ở cạnh đó thay vì để một đường thẳng."""
    h, w = alpha.shape
    n = max(8, round(0.05 * w))
    ramp = np.clip(np.arange(n, dtype=np.float32) / n, 0, 1)
    out = alpha.copy()
    if touches.get("left"):
        out[:, :n] *= ramp[None, :]
    if touches.get("right"):
        out[:, w - n:] *= ramp[::-1][None, :]
    if touches.get("top"):
        m = min(n, h)
        out[:m, :] *= ramp[:m, None]
    return out


def auto_region(seg: Segmenter, image: Image.Image, seed: tuple[int, int] | None) -> tuple[int, int, int, int]:
    alpha = predict_mask(seg, image)
    keep = main_blob(alpha, 0.5, seed)
    ys, xs = np.nonzero(keep)
    w, h = image.size
    mx, my = round(0.03 * w), round(0.03 * h)
    return (max(0, xs.min() - mx), max(0, ys.min() - my), min(w, xs.max() + mx + 1), min(h, ys.max() + my + 1))


def build_still(seg: Segmenter, base: Path, entry: dict, out_dir: Path) -> dict:
    src = ImageOps.exif_transpose(Image.open(base / entry["source"])).convert("RGB")
    w, h = src.size
    seed = tuple(entry["seed"]) if entry.get("seed") else None
    left, top, right, bottom = entry.get("region") or auto_region(seg, src, seed)
    if entry.get("top") is not None:
        top = max(top, int(entry["top"]))
    cut = int(entry["bottom"]) if entry.get("bottom") is not None else h
    # Cho model nhìn sát phần sẽ giữ lại (thêm chút lề dưới) để mép tóc chi tiết hơn.
    bottom = min(bottom, h, cut + round(0.04 * h))

    region = src.crop((left, top, right, bottom))
    alpha = predict_mask(seg, region)
    rgb = np.asarray(region, dtype=np.float32) / 255.0
    alpha = np.clip(guided_filter(rgb.mean(axis=2), alpha, r=4, eps=1e-3), 0, 1)
    lo = float(entry.get("cleanup", 0.08))
    alpha = np.clip((alpha - lo) / (0.92 - lo), 0, 1)
    alpha = np.where(main_blob(alpha, 0.3, (seed[0] - left, seed[1] - top) if seed else None), alpha, 0.0)
    alpha = fill_pinholes(alpha)

    keep_rows = max(1, min(alpha.shape[0], cut - top))
    alpha, rgb = alpha[:keep_rows], rgb[:keep_rows]
    if entry.get("erase"):
        alpha = erase_boxes(alpha, [(l - left, t - top, r - left, b - top) for l, t, r, b in entry["erase"]])
    if entry.get("despillPink"):
        rgb, alpha = despill_pink(rgb, alpha)

    # Người chạm cạnh vùng cắt (do khung ảnh hoặc do "region" cắt bớt) thì làm mờ dần ở cạnh đó.
    edge = 4
    touches = {
        "left": alpha[:, :edge].max(initial=0) > 0.5,
        "right": alpha[:, -edge:].max(initial=0) > 0.5,
        "top": alpha[:edge, :].max(initial=0) > 0.5,
    }
    alpha = fade_cut_edges(alpha, touches)
    rgb = decontaminate(rgb, alpha)

    ys, xs = np.nonzero(alpha > 0.02)
    margin = 6
    x0, x1 = max(0, xs.min() - margin), min(alpha.shape[1], xs.max() + margin + 1)
    y0 = max(0, ys.min() - margin)
    alpha, rgb = alpha[y0:, x0:x1], rgb[y0:, x0:x1]

    rgba = (np.clip(np.dstack([rgb, alpha]), 0, 1) * 255 + 0.5).astype(np.uint8)
    rgba[alpha <= 0.004] = 0  # pixel trong suốt hoàn toàn thì xóa luôn màu
    still = Image.fromarray(rgba, "RGBA")
    if still.height > MAX_OUTPUT_HEIGHT:
        s = MAX_OUTPUT_HEIGHT / still.height
        still = still.resize((round(still.width * s), MAX_OUTPUT_HEIGHT), Image.Resampling.LANCZOS)

    target = out_dir / entry["out"]
    target.parent.mkdir(parents=True, exist_ok=True)
    still.save(target, optimize=True)

    if entry.get("kissAt"):
        # Khung cắt đổi mỗi lần tách lại (model khác, chỉnh vùng), nên tính lại tỷ lệ từ tọa độ trên ảnh gốc.
        kx, ky = entry["kissAt"]
        kiss = {
            "x": round(min(1.0, max(0.0, (kx - left - x0) / alpha.shape[1])), 3),
            "y": round(min(1.0, max(0.0, (ky - top - y0) / alpha.shape[0])), 3),
        }
    else:
        kiss = entry.get("kissPoint", {"x": 0.5, "y": 0.3})
    item = {"file": entry["out"].replace("\\", "/"), "kissPoint": kiss}
    if entry.get("drift") in (-1, 1):
        item["drift"] = entry["drift"]
    print(f"{entry['out']}: {still.width}x{still.height} (vùng {left},{top},{right},{cut}; mờ mép {[k for k, v in touches.items() if v]})")
    return item


def preview_sheet(out_dir: Path, items: list[dict], path: str) -> None:
    """Tấm xem thử: mỗi hình trên nền ca-rô, lưới 10% và chấm đỏ ở điểm tim bay."""
    cells = []
    for it in items:
        im = Image.open(out_dir / it["file"]).convert("RGBA")
        s = 420 / im.height
        im = im.resize((round(im.width * s), 420), Image.Resampling.LANCZOS)
        bg = Image.new("RGBA", im.size, (255, 255, 255, 255))
        d = ImageDraw.Draw(bg)
        for y in range(0, im.height, 14):
            for x in range(0, im.width, 14):
                if (x // 14 + y // 14) % 2:
                    d.rectangle([x, y, x + 13, y + 13], fill=(205, 205, 205, 255))
        bg.alpha_composite(im)
        d = ImageDraw.Draw(bg)
        for k in range(1, 10):
            d.line([(im.width * k / 10, 0), (im.width * k / 10, im.height)], fill=(0, 160, 255, 110))
            d.line([(0, im.height * k / 10), (im.width, im.height * k / 10)], fill=(0, 160, 255, 110))
        kx, ky = it["kissPoint"]["x"] * im.width, it["kissPoint"]["y"] * im.height
        d.ellipse([kx - 6, ky - 6, kx + 6, ky + 6], outline=(255, 0, 80, 255), width=3)
        d.rectangle([0, 0, 120, 16], fill=(0, 0, 0, 255))
        d.text((4, 2), it["file"], fill=(255, 255, 255, 255))
        cells.append(bg.convert("RGB"))
    cols = 6
    rows = (len(cells) + cols - 1) // cols
    cw = max(c.width for c in cells) + 8
    sheet = Image.new("RGB", (cw * cols, 428 * rows), (30, 30, 36))
    for i, c in enumerate(cells):
        sheet.paste(c, ((i % cols) * cw, (i // cols) * 428))
    sheet.save(path, quality=88)


def main() -> None:
    # Thông báo có tiếng Việt; khi đầu ra bị chuyển hướng, Windows mặc định dùng cp1252 nên in sẽ lỗi.
    sys.stdout.reconfigure(encoding="utf-8")
    p = argparse.ArgumentParser()
    p.add_argument("config")
    p.add_argument("--model", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--preview", default=None)
    p.add_argument("--only", type=int, default=None, help="chỉ dựng lại ảnh thứ n (đếm từ 1)")
    args = p.parse_args()

    config_path = Path(args.config)
    config = json.loads(config_path.read_text(encoding="utf-8"))
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    seg = Segmenter(args.model)

    entries = config["stills"]
    manifest_path = out_dir / "manifest.json"
    old = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else {}
    old_items = {it["file"]: it for it in old.get("stills", [])}

    items = []
    for i, entry in enumerate(entries, 1):
        if args.only is not None and i != args.only and entry["out"] in old_items:
            item = dict(old_items[entry["out"]])
            item["kissPoint"] = entry.get("kissPoint", item["kissPoint"])
            if entry.get("drift") in (-1, 1):
                item["drift"] = entry["drift"]
            items.append(item)
            continue
        items.append(build_still(seg, config_path.parent, entry, out_dir))

    manifest = {
        "schema": 1,
        "name": config.get("name", "Crush"),
        "version": config.get("version", "1.0.0"),
        "still": items[0]["file"],
        "fps": config.get("fps", 24),
        "kissPoint": items[0]["kissPoint"],
        "stills": items,
        "clips": config.get("clips", {}),
    }
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if args.preview:
        preview_sheet(out_dir, items, args.preview)
    print(f"Đã ghi {manifest_path} với {len(items)} hình.")


if __name__ == "__main__":
    main()
