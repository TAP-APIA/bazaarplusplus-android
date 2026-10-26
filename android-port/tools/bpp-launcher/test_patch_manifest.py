import struct
import sys
import zipfile
from pathlib import Path
from patch_manifest import (
    BPP_ACTIVITY_NAME,
    BPP_APP_LABEL,
    ORIG_MAIN_ACTIVITY_NAME,
    RES_XML_END_ELEMENT_TYPE,
    RES_XML_START_ELEMENT_TYPE,
    UNITY_WINDOW_METADATA,
    _get_start_element_info,
    _parse_string_pool,
    patch_manifest_bytes,
)


def _collect_activities(axml: bytes) -> tuple[str, list[tuple[str, str, list[str]]]]:
    _, _, pool_size = struct.unpack_from("<HHI", axml, 8)
    strings = _parse_string_pool(axml[8 : 8 + pool_size])
    off = 8 + pool_size
    app_label = ""
    activities: list[tuple[str, str, list[str]]] = []
    cur_act = None
    cur_exp = ""
    cur_actions: list[str] = []
    depth = 0
    act_depth = -1
    while off < len(axml):
        ct, ch, cs = struct.unpack_from("<HHI", axml, off)
        chunk = axml[off : off + cs]
        if ct == RES_XML_START_ELEMENT_TYPE:
            el, attrs = _get_start_element_info(chunk, strings)
            if el == "application":
                app_label = attrs.get("label", "")
            elif el == "activity":
                cur_act = attrs.get("name", "")
                cur_exp = attrs.get("exported", "")
                cur_actions = []
                act_depth = depth
            elif cur_act is not None and el in ("action", "category"):
                cur_actions.append(f"{el}:{attrs.get('name', '')}")
            depth += 1
        elif ct == RES_XML_END_ELEMENT_TYPE:
            depth -= 1
            if cur_act is not None and depth == act_depth:
                activities.append((cur_act, cur_exp, list(cur_actions)))
                cur_act = None
        off += cs
    return app_label, activities


def _read_manifest_version(axml: bytes) -> tuple[int, str]:
    _, _, pool_size = struct.unpack_from("<HHI", axml, 8)
    strings = _parse_string_pool(axml[8 : 8 + pool_size])
    off = 8 + pool_size
    while off < len(axml):
        ct, ch, cs = struct.unpack_from("<HHI", axml, off)
        chunk = axml[off : off + cs]
        if ct == RES_XML_START_ELEMENT_TYPE:
            el, attrs = _get_start_element_info(chunk, strings)
            if el == "manifest":
                return int(attrs["versionCode"], 16), attrs["versionName"]
        off += cs
    raise AssertionError("manifest element not found")


def _collect_metadata(axml: bytes) -> dict[str, str]:
    _, _, pool_size = struct.unpack_from("<HHI", axml, 8)
    strings = _parse_string_pool(axml[8 : 8 + pool_size])
    off = 8 + pool_size
    depth = 0
    application_depth = -1
    metadata: dict[str, str] = {}
    while off < len(axml):
        ct, ch, cs = struct.unpack_from("<HHI", axml, off)
        chunk = axml[off : off + cs]
        if ct == RES_XML_START_ELEMENT_TYPE:
            el, attrs = _get_start_element_info(chunk, strings)
            if el == "application":
                application_depth = depth
            elif el == "meta-data" and depth == application_depth + 1:
                metadata[attrs.get("name", "")] = attrs.get("value", "")
            depth += 1
        elif ct == RES_XML_END_ELEMENT_TYPE:
            depth -= 1
        off += cs
    return metadata


def main() -> int:
    apk_path = (
        Path(sys.argv[1])
        if len(sys.argv) > 1
        else Path(__file__).resolve().parents[3] / "dist" / "app-bpp-fixed.apk"
    )
    with zipfile.ZipFile(apk_path) as zf:
        original = zf.read("AndroidManifest.xml")

    patched = patch_manifest_bytes(
        original, version_code=2, version_name="6.0.0-preview.2", allow_repack=True
    )
    app_label, activities = _collect_activities(patched)
    assert app_label == BPP_APP_LABEL, f"Unexpected label: {app_label}"
    version_code, version_name = _read_manifest_version(patched)
    assert version_code == 2, f"Unexpected versionCode: {version_code}"
    assert version_name == "6.0.0-preview.2", f"Unexpected versionName: {version_name}"
    metadata = _collect_metadata(patched)
    for name in UNITY_WINDOW_METADATA:
        assert metadata.get(name) == "0xffffffff", f"Missing or non-boolean Unity metadata: {name}"

    by_name = {name: (exp, actions) for name, exp, actions in activities}
    assert BPP_ACTIVITY_NAME in by_name, "Missing BppLauncherActivity"
    assert ORIG_MAIN_ACTIVITY_NAME in by_name, "Missing preserved MainActivity"
    assert "com.bepinex.android.BootstrapActivity" in by_name, "Missing BootstrapActivity"
    assert "com.bepinex.android.StubActivity" in by_name, "Missing StubActivity"

    bpp_exp, bpp_actions = by_name[BPP_ACTIVITY_NAME]
    assert bpp_exp == "0xffffffff", f"BppLauncherActivity must be exported: {bpp_exp}"
    assert "action:android.intent.action.MAIN" in bpp_actions
    assert "category:android.intent.category.LAUNCHER" in bpp_actions

    orig_exp, orig_actions = by_name[ORIG_MAIN_ACTIVITY_NAME]
    assert orig_exp == "0x0", f"Original MainActivity must not be exported: {orig_exp}"
    assert orig_actions == [], f"Original MainActivity must not keep LAUNCHER filter: {orig_actions}"

    # Rejects double-patching
    double_rejected = False
    try:
        patch_manifest_bytes(patched)
    except ValueError:
        double_rejected = True
    assert double_rejected, "Expected double-patching to be rejected"

    repacked = patch_manifest_bytes(patched, version_code=38, version_name="6.0.0-preview.38", allow_repack=True)
    assert _read_manifest_version(repacked) == (38, "6.0.0-preview.38")
    assert _collect_activities(repacked) == _collect_activities(patched), "Repack changed activities or filters"
    stable = patch_manifest_bytes(repacked, version_code=39, version_name="6.0.0", allow_repack=True)
    assert _read_manifest_version(stable) == (39, "6.0.0")
    assert _collect_activities(stable) == _collect_activities(patched)
    assert _collect_metadata(stable) == _collect_metadata(patched)

    print("PASS: AndroidManifest binary patcher tests succeeded.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
