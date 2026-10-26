"""Replaces the launcher's adaptive/legacy icon entries with the Bazaar white icon.

Usage:
  replace_icon.py <apk> <icon-dir>

Only the PNG payloads change. AndroidManifest.xml and resources.arsc stay byte-identical, so
the existing @mipmap/ic_launcher / ic_launcher_round references and resource IDs keep resolving.
"""

import struct
import sys
import tempfile
import zipfile
from pathlib import Path

ADAPTIVE = {
    "res/QZ.png": "adaptive-foreground-mdpi.png",
    "res/zr.png": "adaptive-foreground-hdpi.png",
    "res/Em.png": "adaptive-foreground-xhdpi.png",
    "res/Lf.png": "adaptive-foreground-xxhdpi.png",
    "res/as.png": "adaptive-foreground-xxxhdpi.png",
}
LEGACY_SQUARE = {
    "res/9w.png": "legacy-square-mdpi.png",
    "res/8c.png": "legacy-square-hdpi.png",
    "res/FS.png": "legacy-square-xhdpi.png",
    "res/RJ.png": "legacy-square-xxhdpi.png",
    "res/Gc.png": "legacy-square-xxxhdpi.png",
}
LEGACY_ROUND = {
    "res/zR.png": "legacy-round-mdpi.png",
    "res/yn.png": "legacy-round-hdpi.png",
    "res/wb.png": "legacy-round-xhdpi.png",
    "res/fO.png": "legacy-round-xxhdpi.png",
    "res/o-.png": "legacy-round-xxxhdpi.png",
}

EXPECTED_SIZE = {
    "mdpi": 48,
    "hdpi": 72,
    "xhdpi": 96,
    "xxhdpi": 144,
    "xxxhdpi": 192,
}
EXPECTED_ADAPTIVE = {
    "mdpi": 108,
    "hdpi": 162,
    "xhdpi": 216,
    "xxhdpi": 324,
    "xxxhdpi": 432,
}


def png_size(data: bytes) -> tuple[int, int]:
    if len(data) < 24 or data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("Icon entry is not a PNG")
    return struct.unpack(">II", data[16:24])


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    apk = Path(sys.argv[1])
    icon_dir = Path(sys.argv[2])

    replacements: dict[str, bytes] = {}
    for name, filename in ADAPTIVE.items():
        data = (icon_dir / filename).read_bytes()
        density = filename.split("-")[-1].removesuffix(".png")
        expected = EXPECTED_ADAPTIVE[density]
        if png_size(data) != (expected, expected):
            raise SystemExit(f"{filename}: expected {expected}x{expected}, got {png_size(data)}")
        replacements[name] = data
    for mapping, sizes in ((LEGACY_SQUARE, EXPECTED_SIZE), (LEGACY_ROUND, EXPECTED_SIZE)):
        for name, filename in mapping.items():
            data = (icon_dir / filename).read_bytes()
            density = filename.split("-")[-1].removesuffix(".png")
            expected = sizes[density]
            if png_size(data) != (expected, expected):
                raise SystemExit(
                    f"{filename}: expected {expected}x{expected}, got {png_size(data)}"
                )
            replacements[name] = data

    if not apk.is_file():
        raise SystemExit(f"APK not found: {apk}")

    with zipfile.ZipFile(apk) as source:
        names = set(source.namelist())
        missing = sorted(set(replacements) - names)
        if missing:
            raise SystemExit(f"APK is missing expected icon entries: {missing}")
        if "resources.arsc" not in names:
            raise SystemExit("APK is missing resources.arsc")
        arsc = source.read("resources.arsc")

        handle, temp_name = tempfile.mkstemp(suffix=".apk", dir=apk.parent)
        import os

        os.close(handle)
        Path(temp_name).unlink()
        with zipfile.ZipFile(temp_name, "w") as target:
            for info in source.infolist():
                data = replacements.get(info.filename)
                if data is None:
                    data = source.read(info.filename)
                copy = zipfile.ZipInfo(info.filename, date_time=info.date_time)
                copy.compress_type = info.compress_type
                copy.external_attr = info.external_attr
                copy.create_system = info.create_system
                copy.comment = info.comment
                target.writestr(copy, data)

    with zipfile.ZipFile(temp_name) as result:
        if result.read("resources.arsc") != arsc:
            raise SystemExit("Icon replacement changed resources.arsc")
        for name, data in replacements.items():
            if result.read(name) != data:
                raise SystemExit(f"Icon replacement failed for {name}")

    Path(temp_name).replace(apk)
    print(f"Replaced {len(replacements)} launcher icon entries.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
