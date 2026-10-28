import struct
import sys
import zipfile
from pathlib import Path
from patch_manifest import (
    BPP_ACTIVITY_NAME,
    BPP_APP_LABEL,
    FGS_SUBTYPE,
    GAME_APPLICATION,
    GAME_SERVICE,
    ORIG_MAIN_ACTIVITY_NAME,
    RES_XML_END_ELEMENT_TYPE,
    RES_XML_RESOURCE_MAP_TYPE,
    RES_XML_START_ELEMENT_TYPE,
    UNITY_WINDOW_METADATA,
    _get_start_element_info,
    _parse_string_pool,
    patch_manifest_bytes,
    verify_attribute_order,
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


def _application_name(axml: bytes) -> str:
    _, _, pool_size = struct.unpack_from("<HHI", axml, 8)
    strings = _parse_string_pool(axml[8 : 8 + pool_size])
    off = 8 + pool_size
    while off < len(axml):
        ct, _ch, cs = struct.unpack_from("<HHI", axml, off)
        chunk = axml[off : off + cs]
        if ct == RES_XML_START_ELEMENT_TYPE:
            el, attrs = _get_start_element_info(chunk, strings)
            if el == "application":
                return attrs.get("name", "")
        off += cs
    return ""


def _collect_services(axml: bytes) -> list[dict[str, str]]:
    _, _, pool_size = struct.unpack_from("<HHI", axml, 8)
    strings = _parse_string_pool(axml[8 : 8 + pool_size])
    off = 8 + pool_size
    services: list[dict[str, str]] = []
    depth = 0
    current: dict[str, str] | None = None
    service_depth = -1
    while off < len(axml):
        ct, _ch, cs = struct.unpack_from("<HHI", axml, off)
        chunk = axml[off : off + cs]
        if ct == RES_XML_START_ELEMENT_TYPE:
            el, attrs = _get_start_element_info(chunk, strings)
            if el == "service":
                current = dict(attrs)
                services.append(current)
                service_depth = depth
            elif current is not None and el == "property" and depth == service_depth + 1:
                if attrs.get("name") == "android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE":
                    current["subtype"] = attrs.get("value", "")
            depth += 1
        elif ct == RES_XML_END_ELEMENT_TYPE:
            depth -= 1
            if current is not None and depth == service_depth:
                current = None
                service_depth = -1
        off += cs
    return services


def _collect_providers(axml: bytes) -> dict[str, dict[str, str]]:
    _, _, pool_size = struct.unpack_from("<HHI", axml, 8)
    strings = _parse_string_pool(axml[8 : 8 + pool_size])
    off = 8 + pool_size
    providers: dict[str, dict[str, str]] = {}
    while off < len(axml):
        ct, _ch, cs = struct.unpack_from("<HHI", axml, off)
        chunk = axml[off : off + cs]
        if ct == RES_XML_START_ELEMENT_TYPE:
            el, attrs = _get_start_element_info(chunk, strings)
            if el == "provider" and attrs.get("name"):
                providers[attrs["name"]] = attrs
        off += cs
    return providers


def _read_resource_map(axml: bytes) -> list[int]:
    off = 8
    while off < len(axml):
        ct, ch, cs = struct.unpack_from("<HHI", axml, off)
        if cs < 8 or off + cs > len(axml):
            raise AssertionError("Corrupt AXML chunk")
        if ct == RES_XML_RESOURCE_MAP_TYPE:
            count = (cs - ch) // 4
            if count == 0:
                return []
            return list(struct.unpack_from(f"<{count}I", axml, off + ch))
        off += cs
    raise AssertionError("resource map chunk not found")


def _assert_attribute_order_and_indices(axml: bytes) -> None:
    _, _, pool_size = struct.unpack_from("<HHI", axml, 8)
    strings = _parse_string_pool(axml[8 : 8 + pool_size])
    resource_map = _read_resource_map(axml)
    problems = verify_attribute_order(axml)
    assert problems == [], f"Unsorted attributes: {problems}"

    off = 8
    while off < len(axml):
        ct, ch, cs = struct.unpack_from("<HHI", axml, off)
        if cs < 8 or off + cs > len(axml):
            raise AssertionError("Corrupt AXML chunk")
        if ct == RES_XML_START_ELEMENT_TYPE:
            chunk = axml[off : off + cs]
            _line, _comment, _ns, name, attr_start, attr_size, attr_count = struct.unpack_from(
                "<IIIIHHH", chunk, 8
            )
            id_index, class_index, style_index = struct.unpack_from("<HHH", chunk, 30)
            element_name = strings[name] if 0 <= name < len(strings) else f"#{name}"
            records: list[tuple[str, int | None]] = []
            for i in range(attr_count):
                ao = ch + attr_start + i * attr_size
                _ans, aname, _araw, _asize, _azero, _atype, _adata = struct.unpack_from(
                    "<IIIHBBI", chunk, ao
                )
                attr_name = strings[aname] if 0 <= aname < len(strings) else f"#{aname}"
                resource_id = (
                    resource_map[aname]
                    if 0 <= aname < len(resource_map) and resource_map[aname]
                    else None
                )
                records.append((attr_name, resource_id))
            keys = [(0, rid) if rid is not None else (1, 0) for _, rid in records]
            assert keys == sorted(keys), f"Unsorted attributes in <{element_name}>: {records}"
            for label, index, expected_name in (
                ("idIndex", id_index, "id"),
                ("classIndex", class_index, "class"),
                ("styleIndex", style_index, "style"),
            ):
                assert 0 <= index <= attr_count, f"{label} out of range in <{element_name}>"
                if index:
                    assert records[index - 1][0] == expected_name, (
                        f"{label} in <{element_name}> points to {records[index - 1][0]!r}, "
                        f"expected {expected_name!r}"
                    )
        off += cs


def _assert_game_process_components(axml: bytes) -> None:
    assert _application_name(axml) == GAME_APPLICATION, _application_name(axml)

    providers = _collect_providers(axml)
    game = providers.get("com.bepinex.android.bpp.BppGameProcess")
    assert game is not None, f"Missing :game diagnostics provider: {providers}"
    assert game.get("process") == ":game", game
    assert game.get("exported") == "0x0", game
    assert game.get("authorities") == "com.bepinex.android.bpp.game-process", game

    services = [item for item in _collect_services(axml) if item.get("name") == GAME_SERVICE]
    assert len(services) == 1, services
    assert services[0].get("process") == ":game", services[0]
    assert services[0].get("exported") == "0x0", services[0]
    assert services[0].get("foregroundServiceType") == "0x40000000", services[0]
    assert services[0].get("subtype") == FGS_SUBTYPE, services[0]


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
    _assert_attribute_order_and_indices(patched)
    _assert_game_process_components(patched)
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
    _assert_attribute_order_and_indices(repacked)
    _assert_game_process_components(repacked)
    assert _read_manifest_version(repacked) == (38, "6.0.0-preview.38")
    assert _collect_activities(repacked) == _collect_activities(patched), "Repack changed activities or filters"
    stable = patch_manifest_bytes(repacked, version_code=39, version_name="6.0.0", allow_repack=True)
    _assert_attribute_order_and_indices(stable)
    _assert_game_process_components(stable)
    assert _read_manifest_version(stable) == (39, "6.0.0")
    providers = _collect_providers(stable)
    game = providers.get("com.bepinex.android.bpp.BppGameProcess")
    assert game is not None, f"Missing :game diagnostics provider: {providers}"
    assert game.get("process") == ":game", game
    assert game.get("exported") == "0x0", game
    assert game.get("authorities") == "com.bepinex.android.bpp.game-process", game
    again = patch_manifest_bytes(stable, version_code=40, version_name="6.0.0-preview.40", allow_repack=True)
    _assert_attribute_order_and_indices(again)
    _assert_game_process_components(again)
    assert list(_collect_providers(again)).count("com.bepinex.android.bpp.BppGameProcess") == 1
    assert _application_name(stable) == GAME_APPLICATION, _application_name(stable)
    assert _application_name(again) == GAME_APPLICATION
    services = [item for item in _collect_services(stable) if item.get("name") == GAME_SERVICE]
    assert len(services) == 1, services
    assert services[0].get("process") == ":game", services[0]
    assert services[0].get("exported") == "0x0", services[0]
    assert services[0].get("foregroundServiceType") == "0x40000000", services[0]
    assert services[0].get("subtype") == FGS_SUBTYPE, services[0]
    assert len([item for item in _collect_services(again) if item.get("name") == GAME_SERVICE]) == 1
    assert _collect_activities(stable) == _collect_activities(patched)
    assert _collect_metadata(stable) == _collect_metadata(patched)

    from patch_manifest import ensure_install_permission, _has_component
    permission = "android.permission.REQUEST_INSTALL_PACKAGES"
    assert _has_component(again, "uses-permission", permission)
    assert ensure_install_permission(again) == again, "Permission duplicated on repack"
    permission_manifest = ensure_install_permission(original)
    assert _has_component(permission_manifest, "uses-permission", permission)
    assert ensure_install_permission(permission_manifest) == permission_manifest

    print("PASS: AndroidManifest binary patcher tests succeeded.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

