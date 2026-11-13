"""Gói thư mục publish (osx-arm64) thành CrushGheChoi.app trong một tệp zip, chạy được từ Windows.

    python -P packaging/make_mac_app.py dist/mac-publish dist/CrushGheChoi-0.3.0-mac-arm64.zip 0.3.0

- Tệp zip giữ quyền chạy kiểu Unix (zip tạo bằng công cụ của Windows làm mất quyền này và app sẽ không mở được).
- Kiểm tra trước khi gói: tệp chạy và mọi thư viện phải là Mach-O arm64 có chữ ký hợp lệ (máy Mac chip Apple từ chối
  chạy mã không có chữ ký), và trong gói không có ảnh nào ngoài hình mẫu.
"""

from __future__ import annotations

import hashlib
import struct
import sys
import zipfile
from pathlib import Path

BUNDLE = "CrushGheChoi.app"
BUNDLE_ID = "com.vienglacial.crushghechoi"
MIN_MACOS = "12.0"
ALLOWED_IMAGES = {"Assets/Crush/default/still.png"}

MH_MAGIC_64 = 0xFEEDFACF
FAT_MAGIC = 0xCAFEBABE
CPU_TYPE_ARM64 = 0x0100000C
LC_CODE_SIGNATURE = 0x1D
CSMAGIC_EMBEDDED_SIGNATURE = 0xFADE0CC0
CSMAGIC_CODEDIRECTORY = 0xFADE0C02
CS_ADHOC = 0x2


def check_macho(path: Path) -> str:
    """Kiểm tra tệp Mach-O arm64 và chữ ký của nó (băm lại từng trang rồi so với chữ ký). Trả về mô tả ngắn."""
    data = path.read_bytes()

    # Tệp gộp nhiều kiến trúc (universal): lấy riêng phần arm64 để kiểm tra.
    if struct.unpack_from(">I", data, 0)[0] == FAT_MAGIC:
        count = struct.unpack_from(">I", data, 4)[0]
        for i in range(count):
            cpu, _, start, size, _ = struct.unpack_from(">IIIII", data, 8 + 20 * i)
            if cpu == CPU_TYPE_ARM64:
                data = data[start:start + size]
                break
        else:
            raise SystemExit(f"{path.name}: tệp universal không có phần arm64")

    magic, cputype = struct.unpack_from("<II", data, 0)
    if magic != MH_MAGIC_64:
        raise SystemExit(f"{path.name}: không phải Mach-O 64-bit (magic {magic:#x})")
    if cputype != CPU_TYPE_ARM64:
        raise SystemExit(f"{path.name}: không phải bản arm64 (cputype {cputype:#x})")

    ncmds = struct.unpack_from("<I", data, 16)[0]
    offset = 32
    signature = None
    for _ in range(ncmds):
        cmd, size = struct.unpack_from("<II", data, offset)
        if cmd == LC_CODE_SIGNATURE:
            signature = struct.unpack_from("<II", data, offset + 8)
        offset += size
    if signature is None:
        raise SystemExit(f"{path.name}: KHÔNG có chữ ký. Máy Mac chip Apple sẽ không chạy tệp này.")

    sig_off, sig_size = signature
    blob_magic, _, count = struct.unpack_from(">III", data, sig_off)
    if blob_magic != CSMAGIC_EMBEDDED_SIGNATURE:
        raise SystemExit(f"{path.name}: chữ ký không đúng định dạng")

    directory = None
    for i in range(count):
        _, blob_off = struct.unpack_from(">II", data, sig_off + 12 + 8 * i)
        if struct.unpack_from(">I", data, sig_off + blob_off)[0] == CSMAGIC_CODEDIRECTORY:
            directory = sig_off + blob_off
            break
    if directory is None:
        raise SystemExit(f"{path.name}: chữ ký thiếu CodeDirectory")

    (_, _, _, flags, hash_offset, _, _, n_code_slots, code_limit) = struct.unpack_from(">IIIIIIIII", data, directory)
    hash_size, hash_type, _, page_shift = struct.unpack_from(">BBBB", data, directory + 36)
    hashers = {1: (hashlib.sha1, 20), 2: (hashlib.sha256, 32)}  # chữ ký cũ dùng SHA-1, mới dùng SHA-256
    if hash_type not in hashers or hashers[hash_type][1] != hash_size:
        raise SystemExit(f"{path.name}: chữ ký dùng kiểu băm lạ ({hash_type})")
    hasher = hashers[hash_type][0]
    if code_limit != sig_off:
        raise SystemExit(f"{path.name}: chữ ký không phủ hết tệp ({code_limit} / {sig_off})")

    page = 1 << page_shift
    for slot in range(n_code_slots):
        start = slot * page
        expected = data[directory + hash_offset + slot * hash_size: directory + hash_offset + (slot + 1) * hash_size]
        if hasher(data[start:min(start + page, code_limit)]).digest() != expected:
            raise SystemExit(f"{path.name}: chữ ký KHÔNG khớp nội dung (trang {slot}). Tệp đã bị sửa sau khi ký.")

    return "ad-hoc" if flags & CS_ADHOC else "đã ký"


def info_plist(version: str) -> str:
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Crush ghé chơi</string>
    <key>CFBundleDisplayName</key>
    <string>Crush ghé chơi</string>
    <key>CFBundleIdentifier</key>
    <string>{BUNDLE_ID}</string>
    <key>CFBundleExecutable</key>
    <string>CrushGheChoi</string>
    <key>CFBundleIconFile</key>
    <string>app.icns</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleVersion</key>
    <string>{version}</string>
    <key>CFBundleShortVersionString</key>
    <string>{version}</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>{MIN_MACOS}</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
"""


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8")
    publish, out, version = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
    root = Path(__file__).resolve().parent.parent
    files = sorted(p for p in publish.rglob("*") if p.is_file())
    names = {p.relative_to(publish).as_posix() for p in files}
    if "CrushGheChoi" not in names:
        raise SystemExit("Thiếu tệp chạy CrushGheChoi trong thư mục publish.")

    # Không ảnh nào ngoài hình mẫu được lọt vào gói.
    stray = sorted(n for n in names if n.lower().endswith((".png", ".jpg", ".jpeg", ".heic", ".webp")) and n not in ALLOWED_IMAGES)
    if stray:
        raise SystemExit(f"Có ảnh lạ trong gói: {stray}")

    executables = set()
    for p in files:
        rel = p.relative_to(publish).as_posix()
        if p.suffix == ".dylib" or rel in ("CrushGheChoi", "createdump"):
            kind = check_macho(p)
            executables.add(rel)
            print(f"  chữ ký hợp lệ ({kind}): {rel}")

    out.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        def add(arcname: str, data: bytes, mode: int) -> None:
            info = zipfile.ZipInfo(arcname, date_time=(2026, 1, 1, 0, 0, 0))
            info.create_system = 3  # Unix: để macOS đọc quyền truy cập trong external_attr
            info.external_attr = (0o100000 | mode) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)

        add(f"{BUNDLE}/Contents/Info.plist", info_plist(version).encode("utf-8"), 0o644)
        add(f"{BUNDLE}/Contents/Resources/app.icns", (root / "src/CrushGheChoi.Mac/Assets/app.icns").read_bytes(), 0o644)
        for p in files:
            rel = p.relative_to(publish).as_posix()
            add(f"{BUNDLE}/Contents/MacOS/{rel}", p.read_bytes(), 0o755 if rel in executables else 0o644)
        add("HUONG-DAN-MAC.txt", (root / "packaging/HUONG-DAN-MAC.txt").read_bytes(), 0o644)

    print(f"Xong: {out} ({out.stat().st_size / 1048576:.0f} MB, {len(files)} tệp trong app)")


if __name__ == "__main__":
    main()
