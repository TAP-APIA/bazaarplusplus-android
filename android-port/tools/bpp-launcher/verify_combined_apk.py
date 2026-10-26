"""Verifies that a combined BPP launcher APK preserves the verified base launcher.

Usage:
  verify_combined_apk.py <base-apk> <combined-apk> <modpack-zip> <safe-runtime> <safe-harmony> [icon-dir]
"""

import hashlib
import io
import struct
import sys
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from patch_manifest import (  # noqa: E402
    BPP_ACTIVITY_NAME,
    BPP_APP_LABEL,
    _get_start_element_info,
    _parse_string_pool,
)
from replace_icon import ADAPTIVE, LEGACY_ROUND, LEGACY_SQUARE  # noqa: E402

ORIGINAL_MAIN_ACTIVITY = "com.bepinex.android.MainActivity"
REQUIRED_ACTIVITIES = {
    BPP_ACTIVITY_NAME,
    ORIGINAL_MAIN_ACTIVITY,
    "com.bepinex.android.BootstrapActivity",
    "com.bepinex.android.StubActivity",
}
ALLOWED_NEW_ENTRIES = {
    "assets/bpp-modpack.zip",
    "assets/bpp-modpack-manifest.tsv",
    "classes3.dex",
}
def is_signature_file(name: str) -> bool:
    return name.startswith("META-INF/") and (
        name.endswith((".SF", ".RSA", ".DSA", ".EC")) or name == "META-INF/MANIFEST.MF"
    )
# The launcher icon is replaced by payload only: the manifest still points at the same adaptive
# icon XML and resources.arsc keeps the same resource IDs and background color.
ICON_ENTRIES = {
    "res/QZ.png",
    "res/zr.png",
    "res/Em.png",
    "res/Lf.png",
    "res/as.png",
    "res/9w.png",
    "res/8c.png",
    "res/FS.png",
    "res/RJ.png",
    "res/Gc.png",
    "res/zR.png",
    "res/yn.png",
    "res/wb.png",
    "res/fO.png",
    "res/o-.png",
}
ICON_ASSET = {**ADAPTIVE, **LEGACY_SQUARE, **LEGACY_ROUND}
if set(ICON_ASSET) != ICON_ENTRIES:
    raise SystemExit("Icon replacement and verification lists diverged")


def png_size(data: bytes) -> tuple[int, int]:
    if len(data) < 24 or data[:8] != b"\x89PNG\r\n\x1a\n":
        raise SystemExit("Launcher icon entry is not a PNG")
    return struct.unpack(">II", data[16:24])


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_manifest(axml: bytes):
    _, _, pool_size = struct.unpack_from("<HHI", axml, 8)
    strings = _parse_string_pool(axml[8 : 8 + pool_size])
    off = 8 + pool_size
    app_label = None
    version_code = None
    version_name = None
    activities = {}
    cur_activity = None
    cur_exported = None
    cur_actions = []
    depth = 0
    activity_depth = -1
    while off < len(axml):
        ct, ch, cs = struct.unpack_from("<HHI", axml, off)
        chunk = axml[off : off + cs]
        if ct == 0x0102:
            name, attrs = _get_start_element_info(chunk, strings)
            if name == "manifest":
                version_code = int(attrs["versionCode"], 16)
                version_name = attrs["versionName"]
            elif name == "application":
                app_label = attrs.get("label")
            elif name == "activity":
                cur_activity = attrs.get("name")
                cur_exported = attrs.get("exported")
                cur_actions = []
                activity_depth = depth
            elif cur_activity is not None and name in ("action", "category"):
                cur_actions.append(f"{name}:{attrs.get('name')}")
            depth += 1
        elif ct == 0x0103:
            depth -= 1
            if cur_activity is not None and depth == activity_depth:
                activities[cur_activity] = (cur_exported, list(cur_actions))
                cur_activity = None
        off += cs
    return app_label, version_code, version_name, activities


def main() -> int:
    if len(sys.argv) not in (6, 7):
        print(__doc__.strip(), file=sys.stderr)
        return 2
    base = Path(sys.argv[1])
    combined = Path(sys.argv[2])
    modpack_zip = Path(sys.argv[3])
    safe_runtime = Path(sys.argv[4]).read_bytes()
    safe_harmony = Path(sys.argv[5]).read_bytes()
    icon_dir = Path(sys.argv[6]) if len(sys.argv) == 7 else None

    with zipfile.ZipFile(base) as zb, zipfile.ZipFile(combined) as zc:
        base_names = set(zb.namelist())
        combined_names = set(zc.namelist())
        missing = {name for name in base_names - combined_names if not is_signature_file(name)}
        if missing:
            raise SystemExit(f"Combined APK lost base entries: {sorted(missing)}")
        new = {name for name in combined_names - base_names if not is_signature_file(name)}
        if new != ALLOWED_NEW_ENTRIES - base_names:
            raise SystemExit(f"Unexpected new APK entries: {sorted(new)}")

        native = [name for name in base_names if name.startswith("lib/") and name.endswith(".so")]
        if len(native) < 15:
            raise SystemExit(f"Expected at least 15 native libraries in base, found {len(native)}")
        for name in native:
            if zb.read(name) != zc.read(name):
                raise SystemExit(f"Native library changed unexpectedly: {name}")

        for name in ("classes.dex", "classes2.dex", "resources.arsc"):
            if zb.read(name) != zc.read(name):
                raise SystemExit(f"Base entry changed unexpectedly: {name}")

        for name in base_names:
            if name in {"AndroidManifest.xml", "assets/BepInEx.Android.zip"}:
                continue
            if is_signature_file(name) or name in ALLOWED_NEW_ENTRIES:
                continue
            if name in ICON_ENTRIES:
                if icon_dir is None and zb.read(name) == zc.read(name):
                    raise SystemExit(f"Launcher icon was not replaced: {name}")
                png_size(zc.read(name))
                continue
            if zb.read(name) != zc.read(name):
                raise SystemExit(f"Existing entry changed unexpectedly: {name}")

        if icon_dir is not None:
            for name in ICON_ENTRIES:
                replacement = icon_dir / ICON_ASSET[name]
                if zc.read(name) != replacement.read_bytes():
                    raise SystemExit(f"Launcher icon does not match the packaged asset: {name}")

        with zipfile.ZipFile(io.BytesIO(zb.read("assets/BepInEx.Android.zip"))) as zbo:
            with zipfile.ZipFile(io.BytesIO(zc.read("assets/BepInEx.Android.zip"))) as zbz:
                if set(zbo.namelist()) != set(zbz.namelist()):
                    raise SystemExit("Embedded BepInEx archive entry set changed")
                changed = sorted(
                    name for name in zbo.namelist() if zbo.read(name) != zbz.read(name)
                )
                if changed not in (
                    [],
                    ["core/Il2CppInterop.HarmonySupport.dll"],
                    ["core/Il2CppInterop.HarmonySupport.dll", "core/Il2CppInterop.Runtime.dll"],
                    ["core/Il2CppInterop.Runtime.dll"],
                ):
                    raise SystemExit(f"Unexpected embedded BepInEx changes: {changed}")
                if zbz.read("core/Il2CppInterop.Runtime.dll") != safe_runtime:
                    raise SystemExit("Embedded BepInEx runtime is not the production-patched copy")
                if zbz.read("core/Il2CppInterop.HarmonySupport.dll") != safe_harmony:
                    raise SystemExit("Embedded BepInEx HarmonySupport is not the production-patched copy")

        app_label, version_code, version_name, activities = read_manifest(zc.read("AndroidManifest.xml"))
        if app_label != BPP_APP_LABEL:
            raise SystemExit(f"Unexpected application label: {app_label}")
        if not isinstance(version_code, int) or version_code < 2:
            raise SystemExit(f"Invalid versionCode: {version_code}")
        if not version_name:
            raise SystemExit("Missing versionName")
        if not REQUIRED_ACTIVITIES.issubset(activities):
            raise SystemExit(f"Missing required activities: {REQUIRED_ACTIVITIES - set(activities)}")
        bpp_exported, bpp_actions = activities[BPP_ACTIVITY_NAME]
        if bpp_exported != "0xffffffff":
            raise SystemExit("BppLauncherActivity must be exported")
        if "action:android.intent.action.MAIN" not in bpp_actions:
            raise SystemExit("BppLauncherActivity is missing MAIN action")
        if "category:android.intent.category.LAUNCHER" not in bpp_actions:
            raise SystemExit("BppLauncherActivity is missing LAUNCHER category")
        orig_exported, orig_actions = activities[ORIGINAL_MAIN_ACTIVITY]
        if orig_exported != "0x0" or orig_actions:
            raise SystemExit("Original MainActivity must be internal and have no launcher filter")

        manifest_lines = zc.read("assets/bpp-modpack-manifest.tsv").decode("utf-8").strip().split("\n")
        header = manifest_lines[0].split("\t")
        if header[0] != "bundle_id":
            raise SystemExit("Invalid embedded modpack manifest header")
        embedded = zc.read("assets/bpp-modpack.zip")
        if sha256(embedded) != header[1]:
            raise SystemExit("Embedded modpack bundle_id does not match its bytes")
        expected = Path(modpack_zip).read_bytes()
        with zipfile.ZipFile(io.BytesIO(expected)) as source_zip, zipfile.ZipFile(io.BytesIO(embedded)) as embedded_zip:
            source_names = {name.replace("\\", "/") for name in source_zip.namelist() if not name.endswith("/")}
            if source_names != set(embedded_zip.namelist()):
                raise SystemExit("Embedded modpack entry list differs from the packaged modpack")
            for name in source_names:
                if source_zip.read(name) != embedded_zip.read(name):
                    raise SystemExit(f"Embedded modpack entry changed: {name}")
            if len(embedded_zip.namelist()) < 60:
                raise SystemExit("Embedded modpack is unexpectedly small")
            if not all(
                name == "modpack.json" or name.startswith(("plugins/", "core/"))
                for name in embedded_zip.namelist()
            ):
                raise SystemExit("Embedded modpack contains a path outside plugins/core")
            forbidden = [
                name
                for name in embedded_zip.namelist()
                if name.lower().endswith((".cfg", ".db", ".sqlite", ".keystore", ".jks", ".mp4", ".png"))
            ]
            if forbidden:
                raise SystemExit(f"Embedded modpack contains forbidden files: {forbidden}")

        if any(name.lower().endswith((".keystore", ".jks")) for name in combined_names):
            raise SystemExit("Combined APK contains signing material")

    print(
        "PASS: base native/DEX/resources preserved, manifest patched, "
       f"versionCode={version_code}, versionName={version_name}, embedded modpack verified."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
