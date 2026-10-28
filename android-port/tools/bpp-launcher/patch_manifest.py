import struct
import sys
from pathlib import Path

RES_STRING_POOL_TYPE = 0x0001
RES_XML_START_ELEMENT_TYPE = 0x0102
RES_XML_END_ELEMENT_TYPE = 0x0103
RES_XML_RESOURCE_MAP_TYPE = 0x0180
TYPE_STRING = 0x03
ANDROID_NS = "http://schemas.android.com/apk/res/android"

BPP_ACTIVITY_NAME = "com.bepinex.android.bpp.BppLauncherActivity"
ORIG_MAIN_ACTIVITY_NAME = "com.bepinex.android.MainActivity"
BPP_APP_LABEL = "BPP Android"
GAME_PROCESS_PROVIDER = "com.bepinex.android.bpp.BppGameProcess"
GAME_PROCESS_AUTHORITY = "com.bepinex.android.bpp.game-process"
GAME_APPLICATION = "com.bepinex.android.bpp.BppGameApplication"
GAME_SERVICE = "com.bepinex.android.bpp.BppGenerationForegroundService"
FGS_PROPERTY = "android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE"
FGS_SUBTYPE = "keep_game_foreground_during_mod_generation"
UNITY_WINDOW_METADATA = ("unity.launch-fullscreen", "unity.render-outside-safearea")


def patch_unity_window_metadata(axml: bytes) -> bytes:
    """Restore Unity's original application window policy in the fused host package.

    Unity resolves these through Activity.getPackageName()/PackageManager, not the
    cloned game manifest. In particular render-outside-safearea controls both modern
    status-bar requests and Unity's root-inset-driven FrameLayout padding.
    """
    pool_size = struct.unpack_from("<I", axml, 12)[0]
    existing = _parse_string_pool(axml[8:8 + pool_size])
    required = ["meta-data", "name", "value", "http://schemas.android.com/apk/res/android", *UNITY_WINDOW_METADATA]
    pool, strings = _rebuild_string_pool_with_appended(
        axml[8:8 + pool_size], [s for s in required if s not in existing]
    )
    idx = {s: strings.index(s) for s in required}
    # Android framework attribute IDs must also be present in the resource map.
    chunks = [pool]
    offset = 8 + pool_size
    depth = 0
    application_depth = -1
    found = set()
    while offset < len(axml):
        kind, _, size = struct.unpack_from("<HHI", axml, offset)
        chunk = axml[offset:offset + size]
        if kind == RES_XML_RESOURCE_MAP_TYPE:
            ids = list(struct.unpack_from(f"{(size - 8) // 4}I", chunk, 8))
            ids.extend([0] * max(0, max(idx["name"], idx["value"]) + 1 - len(ids)))
            ids[idx["name"]] = 0x01010003
            ids[idx["value"]] = 0x01010024
            chunk = struct.pack("<HHI", kind, 8, 8 + 4 * len(ids)) + struct.pack(f"<{len(ids)}I", *ids)
        elif kind == RES_XML_START_ELEMENT_TYPE:
            element, attrs = _get_start_element_info(chunk, strings)
            if element == "application":
                application_depth = depth
            elif depth == application_depth + 1 and element == "meta-data" and attrs.get("name") in UNITY_WINDOW_METADATA:
                found.add(attrs["name"])
                mutable = bytearray(chunk)
                # Do not preserve a previous raw string or integer type for a boolean.
                _, header, _ = struct.unpack_from("<HHI", chunk)
                start, width, count = struct.unpack_from("<HHH", chunk, 24)
                for i in range(count):
                    pos = header + start + i * width
                    if struct.unpack_from("<I", chunk, pos + 4)[0] == idx["value"]:
                        struct.pack_into("<IHBBI", mutable, pos + 8, 0xFFFFFFFF, 8, 0, 0x12, 0xFFFFFFFF)
                chunk = bytes(mutable)
            depth += 1
        elif kind == RES_XML_END_ELEMENT_TYPE:
            depth -= 1
            if depth == application_depth:
                for name in UNITY_WINDOW_METADATA:
                    if name in found:
                        continue
                    ns = idx["http://schemas.android.com/apk/res/android"]
                    attrs = struct.pack("<IIIHBBI", ns, idx["name"], idx[name], 8, 0, TYPE_STRING, idx[name])
                    attrs += struct.pack("<IIIHBBI", ns, idx["value"], 0xFFFFFFFF, 8, 0, 0x12, 0xFFFFFFFF)
                    chunks.append(struct.pack("<HHI4I6H", RES_XML_START_ELEMENT_TYPE, 16, 76, 0, 0xFFFFFFFF,
                                              0xFFFFFFFF, idx["meta-data"], 20, 20, 2, 0, 0, 0) + attrs)
                    chunks.append(struct.pack("<HHI4I", RES_XML_END_ELEMENT_TYPE, 16, 24, 0, 0xFFFFFFFF,
                                              0xFFFFFFFF, idx["meta-data"]))
                application_depth = -1
        chunks.append(chunk)
        offset += size
    body = b"".join(chunks)
    return struct.pack("<HHI", 3, 8, 8 + len(body)) + body


def _encode_utf16_pool_string(text: str) -> bytes:
    raw = text.encode("utf-16le")
    char_len = len(text)
    if char_len >= 0x8000:
        raise ValueError("String too long for simple UTF-16 AXML pool entry")
    return struct.pack("<H", char_len) + raw + b"\x00\x00"


def _parse_string_pool(chunk: bytes) -> list[str]:
    t, header_size, chunk_size = struct.unpack_from("<HHI", chunk, 0)
    if t != RES_STRING_POOL_TYPE:
        raise ValueError(f"Expected string pool chunk, got 0x{t:04x}")
    string_count, style_count, flags, strings_start, styles_start = struct.unpack_from(
        "<5I", chunk, 8
    )
    if style_count != 0 or (flags & 0x100) != 0:
        raise ValueError("Expected UTF-16 string pool without styles")
    offsets = [struct.unpack_from("<I", chunk, 28 + i * 4)[0] for i in range(string_count)]
    strings = []
    for off in offsets:
        pos = strings_start + off
        n = struct.unpack_from("<H", chunk, pos)[0]
        strings.append(chunk[pos + 2 : pos + 2 + n * 2].decode("utf-16le"))
    return strings


def _rebuild_string_pool_with_appended(chunk: bytes, extra_strings: list[str]) -> tuple[bytes, list[str]]:
    t, header_size, chunk_size = struct.unpack_from("<HHI", chunk, 0)
    string_count, style_count, flags, strings_start, styles_start = struct.unpack_from(
        "<5I", chunk, 8
    )
    existing_strings = _parse_string_pool(chunk)
    old_offsets = [struct.unpack_from("<I", chunk, 28 + i * 4)[0] for i in range(string_count)]
    old_blob = chunk[strings_start:chunk_size]
    # Trim trailing alignment padding after the last existing string
    if old_offsets:
        last_pos = old_offsets[-1]
        last_len = struct.unpack_from("<H", old_blob, last_pos)[0]
        used_blob_len = last_pos + 2 + last_len * 2 + 2
        old_blob = old_blob[:used_blob_len]
    else:
        old_blob = b""

    new_offsets = list(old_offsets)
    blob_parts = [old_blob]
    cur_len = len(old_blob)
    all_strings = list(existing_strings)
    for s in extra_strings:
        new_offsets.append(cur_len)
        encoded = _encode_utf16_pool_string(s)
        blob_parts.append(encoded)
        cur_len += len(encoded)
        all_strings.append(s)

    new_string_count = len(new_offsets)
    new_strings_start = 28 + new_string_count * 4
    blob = b"".join(blob_parts)
    total_unaligned = new_strings_start + len(blob)
    pad = (4 - (total_unaligned % 4)) % 4
    blob += b"\x00" * pad
    new_chunk_size = new_strings_start + len(blob)

    header = struct.pack(
        "<HHI5I",
        RES_STRING_POOL_TYPE,
        header_size,
        new_chunk_size,
        new_string_count,
        0,
        flags,
        new_strings_start,
        0,
    )
    offsets_bytes = b"".join(struct.pack("<I", off) for off in new_offsets)
    return header + offsets_bytes + blob, all_strings


def _read_resource_map(axml: bytes) -> list[int]:
    offset = 8
    while offset + 8 <= len(axml):
        kind, header_size, size = struct.unpack_from("<HHI", axml, offset)
        if size < 8 or offset + size > len(axml):
            raise ValueError("Corrupt AXML chunk while reading the resource map")
        if kind == RES_XML_RESOURCE_MAP_TYPE:
            if header_size < 8 or header_size > size:
                raise ValueError("Invalid resource map chunk header")
            count = (size - header_size) // 4
            if header_size + count * 4 != size:
                raise ValueError("Invalid resource map chunk size")
            if count == 0:
                return []
            return list(struct.unpack_from(f"<{count}I", axml, offset + header_size))
        offset += size
    raise ValueError("AndroidManifest.xml is missing the resource map chunk")


def _resource_id_for_name(name_index: int, resource_map: list[int]) -> int | None:
    if 0 <= name_index < len(resource_map):
        resource_id = resource_map[name_index]
        return resource_id if resource_id else None
    return None


def _mapped_string_index(strings: list[str], resource_map: list[int], value: str) -> int:
    for index, text in enumerate(strings):
        if text == value and index < len(resource_map) and resource_map[index] != 0:
            return index
    raise ValueError(
        f"Android attribute name {value!r} is missing a resource id in the manifest resource map"
    )


def _sort_start_element_attributes(chunk: bytes, resource_map: list[int]) -> bytes:
    kind, header_size, size = struct.unpack_from("<HHI", chunk, 0)
    if kind != RES_XML_START_ELEMENT_TYPE:
        return chunk
    if len(chunk) < 36:
        raise ValueError("Truncated start element chunk")
    _line, _comment, _ns, _name, attr_start, attr_size, attr_count = struct.unpack_from(
        "<IIIIHHH", chunk, 8
    )
    id_index, class_index, style_index = struct.unpack_from("<HHH", chunk, 30)
    if attr_count == 0:
        return chunk
    if attr_size < 20:
        raise ValueError(f"Unsupported AXML attribute record size {attr_size}")
    base = header_size + attr_start
    end = base + attr_count * attr_size
    if base < header_size or end > len(chunk):
        raise ValueError("Start element attribute list is out of bounds")

    records = [
        chunk[base + i * attr_size : base + (i + 1) * attr_size]
        for i in range(attr_count)
    ]

    def sort_key(record: bytes) -> tuple[int, int]:
        name_index = struct.unpack_from("<I", record, 4)[0]
        resource_id = _resource_id_for_name(name_index, resource_map)
        if resource_id is None:
            return (1, 0)
        return (0, resource_id)

    order = sorted(range(attr_count), key=lambda i: sort_key(records[i]))
    if order == list(range(attr_count)):
        return chunk

    updated = bytearray(chunk)
    updated[base:end] = b"".join(records[i] for i in order)
    new_positions = {old: new for new, old in enumerate(order)}

    def remap(index: int) -> int:
        if index == 0:
            return 0
        if 1 <= index <= attr_count:
            return new_positions[index - 1] + 1
        return index

    struct.pack_into(
        "<HHH",
        updated,
        30,
        remap(id_index),
        remap(class_index),
        remap(style_index),
    )
    return bytes(updated)


def sort_element_attributes(axml: bytes) -> bytes:
    """Sort every element's attributes by Android resource id.

    Android's ResXMLParser / obtainAttributes requires attributes to be sorted
    ascending by resource id. Attributes without a resource id are placed after all
    mapped attributes while preserving their relative order.
    """
    resource_map = _read_resource_map(axml)
    updated = bytearray(axml)
    offset = 8
    while offset < len(updated):
        kind, _header_size, size = struct.unpack_from("<HHI", updated, offset)
        if size < 8 or offset + size > len(updated):
            raise ValueError("Corrupt AXML chunk while sorting attributes")
        if kind == RES_XML_START_ELEMENT_TYPE:
            updated[offset : offset + size] = _sort_start_element_attributes(
                bytes(updated[offset : offset + size]), resource_map
            )
        offset += size
    return bytes(updated)


def verify_attribute_order(axml: bytes) -> list[str]:
    """Return descriptions of elements whose attributes are not sorted by resource id."""
    problems: list[str] = []
    try:
        resource_map = _read_resource_map(axml)
        pool_size = struct.unpack_from("<I", axml, 12)[0]
        strings = _parse_string_pool(axml[8 : 8 + pool_size])
    except ValueError as exc:
        return [str(exc)]

    offset = 8
    while offset < len(axml):
        if offset + 8 > len(axml):
            problems.append(f"Truncated AXML chunk at offset {offset}")
            break
        kind, _header_size, size = struct.unpack_from("<HHI", axml, offset)
        if size < 8 or offset + size > len(axml):
            problems.append(f"Corrupt AXML chunk at offset {offset}")
            break
        if kind == RES_XML_START_ELEMENT_TYPE:
            chunk = axml[offset : offset + size]
            try:
                _kind, header_size, _size = struct.unpack_from("<HHI", chunk, 0)
                _line, _comment, _ns, name, attr_start, attr_size, attr_count = struct.unpack_from(
                    "<IIIIHHH", chunk, 8
                )
                if attr_count and attr_size < 20:
                    raise ValueError(f"Unsupported AXML attribute record size {attr_size}")
                base = header_size + attr_start
                end = base + attr_count * attr_size
                if attr_count and (base < header_size or end > len(chunk)):
                    raise ValueError("Start element attribute list is out of bounds")
                keys: list[tuple[int, int]] = []
                for i in range(attr_count):
                    record_offset = base + i * attr_size
                    attr_name_index = struct.unpack_from("<I", chunk, record_offset + 4)[0]
                    resource_id = _resource_id_for_name(attr_name_index, resource_map)
                    keys.append((0, resource_id) if resource_id is not None else (1, 0))
                if keys != sorted(keys):
                    element_name = strings[name] if 0 <= name < len(strings) else f"#{name}"
                    problems.append(
                        f"<{element_name}> attributes are not sorted by resource id "
                        f"at chunk 0x{offset:x}"
                    )
            except (struct.error, ValueError) as exc:
                problems.append(f"Invalid start element at offset {offset}: {exc}")
        offset += size
    return problems


def _patch_start_element_attr(
    chunk: bytearray,
    strings: list[str],
    attr_name: str,
    new_string_idx: int | None = None,
    new_bool_int: int | None = None,
    new_int_value: int | None = None,
) -> bool:
    ct, ch, cs = struct.unpack_from("<HHI", chunk, 0)
    if ct != RES_XML_START_ELEMENT_TYPE:
        return False
    line, comment, ns, name, attr_start, attr_size, attr_count = struct.unpack_from(
        "<IIIIHHH", chunk, 8
    )
    for i in range(attr_count):
        ao = ch + attr_start + i * attr_size
        ans, aname, araw, asize, azero, atype, adata = struct.unpack_from("<IIIHBBI", chunk, ao)
        if 0 <= aname < len(strings) and strings[aname] == attr_name:
            if new_string_idx is not None:
                struct.pack_into("<IIIHBBI", chunk, ao, ans, aname, new_string_idx, asize, azero, TYPE_STRING, new_string_idx)
            elif new_bool_int is not None:
                struct.pack_into("<IIIHBBI", chunk, ao, ans, aname, araw, asize, azero, atype, new_bool_int)
            elif new_int_value is not None:
                struct.pack_into("<IIIHBBI", chunk, ao, ans, aname, araw, asize, azero, atype, new_int_value)
            return True
    return False


def _get_start_element_info(chunk: bytes, strings: list[str]) -> tuple[str, dict[str, str]]:
    ct, ch, cs = struct.unpack_from("<HHI", chunk, 0)
    line, comment, ns, name, attr_start, attr_size, attr_count = struct.unpack_from(
        "<IIIIHHH", chunk, 8
    )
    el_name = strings[name] if 0 <= name < len(strings) else f"#{name}"
    attrs: dict[str, str] = {}
    for i in range(attr_count):
        ao = ch + attr_start + i * attr_size
        ans, aname, araw, asize, azero, atype, adata = struct.unpack_from("<IIIHBBI", chunk, ao)
        k = strings[aname] if 0 <= aname < len(strings) else f"#{aname}"
        if araw != 0xFFFFFFFF and 0 <= araw < len(strings):
            v = strings[araw]
        elif atype == TYPE_STRING and 0 <= adata < len(strings):
            v = strings[adata]
        else:
            v = hex(adata)
        attrs[k] = v
    return el_name, attrs


def patch_manifest_bytes(
    axml: bytes,
    version_code: int | None = None,
    version_name: str | None = None,
    *,
    allow_repack: bool = False,
) -> bytes:
    if len(axml) < 16 or struct.unpack_from("<I", axml, 0)[0] != 0x00080003:
        raise ValueError("Invalid binary AndroidManifest.xml header")

    pool_type, pool_header, pool_size = struct.unpack_from("<HHI", axml, 8)
    if pool_type != RES_STRING_POOL_TYPE:
        raise ValueError("First chunk in AndroidManifest.xml is not a string pool")

    existing_strings = _parse_string_pool(axml[8 : 8 + pool_size])
    if version_code is not None and not (0 < version_code < 0x80000000):
        raise ValueError(f"Invalid versionCode: {version_code}")
    if version_name is not None and not version_name.strip():
        raise ValueError("versionName must not be empty")
    if BPP_ACTIVITY_NAME in existing_strings:
        if not allow_repack:
            raise ValueError("Manifest is already patched with BppLauncherActivity")
        return _update_bpp_manifest_version(axml, version_code, version_name)

    extra_strings = [BPP_ACTIVITY_NAME, BPP_APP_LABEL]
    if version_name is not None:
        extra_strings.append(version_name)
    new_pool, strings = _rebuild_string_pool_with_appended(
        axml[8 : 8 + pool_size],
        extra_strings,
    )
    bpp_activity_idx = strings.index(BPP_ACTIVITY_NAME)
    bpp_label_idx = strings.index(BPP_APP_LABEL)
    version_name_idx = strings.index(version_name) if version_name is not None else None

    # Parse remaining chunks
    chunks: list[bytes] = []
    off = 8 + pool_size
    while off < len(axml):
        ct, ch, cs = struct.unpack_from("<HHI", axml, off)
        if cs <= 0 or off + cs > len(axml):
            raise ValueError(f"Corrupt AXML chunk at offset {off}")
        chunks.append(axml[off : off + cs])
        off += cs

    out_chunks: list[bytes] = [new_pool]
    i = 0
    patched_launcher = False
    patched_app_label = False
    while i < len(chunks):
        chunk = chunks[i]
        ct, ch, cs = struct.unpack_from("<HHI", chunk, 0)
        if ct == RES_XML_START_ELEMENT_TYPE:
            el_name, attrs = _get_start_element_info(chunk, strings)
            if el_name == "manifest":
                mut = bytearray(chunk)
                if version_code is not None:
                    if not _patch_start_element_attr(
                        mut, strings, "versionCode", new_int_value=version_code
                    ):
                        raise ValueError("versionCode attribute not found in manifest")
                if version_name_idx is not None:
                    if not _patch_start_element_attr(
                        mut, strings, "versionName", new_string_idx=version_name_idx
                    ):
                        raise ValueError("versionName attribute not found in manifest")
                out_chunks.append(bytes(mut))
                i += 1
                continue
            if el_name == "application":
                mut = bytearray(chunk)
                if _patch_start_element_attr(mut, strings, "label", new_string_idx=bpp_label_idx):
                    patched_app_label = True
                out_chunks.append(bytes(mut))
                i += 1
                continue
            if el_name == "activity" and attrs.get("name") == ORIG_MAIN_ACTIVITY_NAME:
                # Collect the entire <activity>...</activity> subtree
                activity_start = bytearray(chunk)
                subtree = [chunks[i]]
                depth = 1
                j = i + 1
                while j < len(chunks) and depth > 0:
                    sub_ct, _, _ = struct.unpack_from("<HHI", chunks[j], 0)
                    if sub_ct == RES_XML_START_ELEMENT_TYPE:
                        depth += 1
                    elif sub_ct == RES_XML_END_ELEMENT_TYPE:
                        depth -= 1
                    subtree.append(chunks[j])
                    j += 1
                if depth != 0:
                    raise ValueError("Unbalanced <activity> subtree for MainActivity")

                # 1. Primary launcher activity: BppLauncherActivity with original intent-filter
                if not _patch_start_element_attr(
                    activity_start, strings, "name", new_string_idx=bpp_activity_idx
                ):
                    raise ValueError("Failed to patch MainActivity name attribute")
                out_chunks.append(bytes(activity_start))
                for sub in subtree[1:]:
                    out_chunks.append(sub)

                # 2. Secondary internal activity: original MainActivity without intent-filter, exported=false
                orig_start = bytearray(chunk)
                _patch_start_element_attr(orig_start, strings, "exported", new_bool_int=0)
                out_chunks.append(bytes(orig_start))
                out_chunks.append(subtree[-1])  # matching </activity>

                patched_launcher = True
                i = j
                continue
        out_chunks.append(chunk)
        i += 1

    if not patched_launcher:
        raise ValueError("MainActivity element not found in AndroidManifest.xml")
    if not patched_app_label:
        raise ValueError("application label attribute not found in AndroidManifest.xml")

    body = b"".join(out_chunks)
    total_size = 8 + len(body)
    header = struct.pack("<II", 0x00080003, total_size)
    return _install_game_process_hooks(header + body)


def _update_bpp_manifest_version(axml: bytes, version_code: int | None, version_name: str | None) -> bytes:
    """Repackage a verified BPP host without cloning its launcher activity again."""
    pool_size = struct.unpack_from("<I", axml, 12)[0]
    pool, strings = _rebuild_string_pool_with_appended(
        axml[8:8 + pool_size], [version_name] if version_name is not None else []
    )
    chunks = [pool]
    offset = 8 + pool_size
    bpp_activities = 0
    version_updated = False
    while offset < len(axml):
        kind, _, size = struct.unpack_from("<HHI", axml, offset)
        if size < 8 or offset + size > len(axml):
            raise ValueError("Corrupt AXML chunk during repack")
        chunk = bytearray(axml[offset:offset + size])
        if kind == RES_XML_START_ELEMENT_TYPE:
            element, attrs = _get_start_element_info(chunk, strings)
            if element == "activity" and attrs.get("name") == BPP_ACTIVITY_NAME:
                bpp_activities += 1
            if element == "manifest":
                if version_code is not None and not _patch_start_element_attr(
                    chunk, strings, "versionCode", new_int_value=version_code
                ):
                    raise ValueError("versionCode attribute not found")
                if version_name is not None and not _patch_start_element_attr(
                    chunk, strings, "versionName", new_string_idx=len(strings) - 1
                ):
                    raise ValueError("versionName attribute not found")
                version_updated = True
        chunks.append(bytes(chunk))
        offset += size
    if bpp_activities != 1 or not version_updated:
        raise ValueError("Expected exactly one BppLauncherActivity and a manifest")
    body = b"".join(chunks)
    return _install_game_process_hooks(struct.pack("<HHI", 3, 8, len(body) + 8) + body)


def _install_game_process_hooks(axml: bytes) -> bytes:
    """Hooks that must run inside :game while Cpp2IL generates on the grey screen."""
    axml = ensure_generation_service(
        ensure_game_application(ensure_game_process_provider(patch_unity_window_metadata(axml)))
    )
    return sort_element_attributes(ensure_install_permission(axml))


def ensure_install_permission(axml: bytes) -> bytes:
    """Allow the explicit system installer flow; Android still requires user consent."""
    permission = "android.permission.REQUEST_INSTALL_PACKAGES"
    if _has_component(axml, "uses-permission", permission):
        return axml
    axml, strings = _with_extra_strings(axml, ["uses-permission", permission])
    resource_map = _read_resource_map(axml)
    index = strings.index(permission)
    attribute = _android_attr(strings, resource_map, "name", index, TYPE_STRING, index)
    element = strings.index("uses-permission")
    addition = _xml_start(element, attribute, 1) + _xml_end(element)
    offset = 8
    while offset < len(axml):
        kind, _, size = struct.unpack_from("<HHI", axml, offset)
        if size < 8 or offset + size > len(axml):
            raise ValueError("Corrupt AXML while adding install permission")
        if kind == RES_XML_START_ELEMENT_TYPE:
            name, _ = _get_start_element_info(axml[offset:offset + size], strings)
            if name == "manifest":
                end = offset + size
                body = axml[8:end] + addition + axml[end:]
                return struct.pack("<HHI", 3, 8, len(body) + 8) + body
        offset += size
    raise ValueError("manifest element not found while adding install permission")


def _with_extra_strings(axml: bytes, extra: list[str]) -> tuple[bytes, list[str]]:
    pool_size = struct.unpack_from("<I", axml, 12)[0]
    existing = _parse_string_pool(axml[8 : 8 + pool_size])
    missing = [value for value in extra if value not in existing]
    if not missing:
        return axml, existing
    pool, strings = _rebuild_string_pool_with_appended(axml[8 : 8 + pool_size], missing)
    body = pool + axml[8 + pool_size :]
    return struct.pack("<HHI", 3, 8, len(body) + 8) + body, strings


def _append_element_attr(
    chunk: bytes,
    strings: list[str],
    resource_map: list[int],
    attr_name: str,
    raw: int,
    typ: int,
    data: int,
) -> bytes:
    kind, header_size, size = struct.unpack_from("<HHI", chunk, 0)
    if kind != RES_XML_START_ELEMENT_TYPE:
        raise ValueError("Cannot append an attribute to a non-element chunk")
    _line, _comment, _ns, _name, attr_start, attr_size, attr_count = struct.unpack_from("<IIIIHHH", chunk, 8)
    if attr_size != 20:
        raise ValueError(f"Unexpected attribute size {attr_size}")
    pos = header_size + attr_start + attr_count * attr_size
    if pos > len(chunk):
        raise ValueError("Attribute list extends past its element chunk")
    attr = struct.pack(
        "<IIIHBBI",
        strings.index(ANDROID_NS),
        _mapped_string_index(strings, resource_map, attr_name),
        raw,
        8,
        0,
        typ,
        data,
    )
    updated = bytearray(chunk[:pos] + attr + chunk[pos:])
    struct.pack_into("<I", updated, 4, size + len(attr))
    struct.pack_into("<H", updated, 28, attr_count + 1)
    return bytes(updated)


def _has_component(axml: bytes, element_name: str, class_name: str) -> bool:
    pool_size = struct.unpack_from("<I", axml, 12)[0]
    strings = _parse_string_pool(axml[8 : 8 + pool_size])
    offset = 8 + pool_size
    while offset < len(axml):
        kind, _, size = struct.unpack_from("<HHI", axml, offset)
        if size < 8 or offset + size > len(axml):
            break
        if kind == RES_XML_START_ELEMENT_TYPE:
            element, attrs = _get_start_element_info(axml[offset : offset + size], strings)
            if element == element_name and attrs.get("name") == class_name:
                return True
        offset += size
    return False


def ensure_game_application(axml: bytes) -> bytes:
    """Run launcher code in every process, including :game, without a custom provider process."""
    axml, strings = _with_extra_strings(axml, [GAME_APPLICATION])
    if _has_component(axml, "application", GAME_APPLICATION):
        return axml
    resource_map = _read_resource_map(axml)
    class_index = strings.index(GAME_APPLICATION)
    pool_size = struct.unpack_from("<I", axml, 12)[0]
    parts: list[bytes] = []
    offset = 8 + pool_size
    found = False
    while offset < len(axml):
        kind, _, size = struct.unpack_from("<HHI", axml, offset)
        if size < 8 or offset + size > len(axml):
            raise ValueError("Corrupt AXML chunk while setting the game application")
        chunk = axml[offset : offset + size]
        if not found and kind == RES_XML_START_ELEMENT_TYPE:
            element, attrs = _get_start_element_info(chunk, strings)
            if element == "application":
                if attrs.get("name"):
                    return axml
                chunk = _append_element_attr(
                    chunk, strings, resource_map, "name", class_index, TYPE_STRING, class_index
                )
                found = True
        parts.append(chunk)
        offset += size
    if not found:
        raise ValueError("application element not found while setting the game application")
    body = axml[8 : 8 + pool_size] + b"".join(parts)
    return struct.pack("<HHI", 3, 8, len(body) + 8) + body


def _has_provider(axml: bytes, class_name: str) -> bool:
    pool_size = struct.unpack_from("<I", axml, 12)[0]
    strings = _parse_string_pool(axml[8 : 8 + pool_size])
    offset = 8 + pool_size
    while offset < len(axml):
        kind, _, size = struct.unpack_from("<HHI", axml, offset)
        if size < 8 or offset + size > len(axml):
            break
        if kind == RES_XML_START_ELEMENT_TYPE:
            element, attrs = _get_start_element_info(axml[offset : offset + size], strings)
            if element == "provider" and attrs.get("name") == class_name:
                return True
        offset += size
    return False


def _provider_element(strings: list[str], resource_map: list[int]) -> tuple[bytes, bytes]:
    ns = strings.index(ANDROID_NS)
    name_i = _mapped_string_index(strings, resource_map, "name")
    exported_i = _mapped_string_index(strings, resource_map, "exported")
    authorities_i = _mapped_string_index(strings, resource_map, "authorities")
    process_i = _mapped_string_index(strings, resource_map, "process")
    provider_i = strings.index("provider")
    class_i = strings.index(GAME_PROCESS_PROVIDER)
    authority_i = strings.index(GAME_PROCESS_AUTHORITY)
    game_i = strings.index(":game")

    def attr(name_idx: int, raw: int, typ: int, data: int) -> bytes:
        return struct.pack("<IIIHBBI", ns, name_idx, raw, 8, 0, typ, data)

    attrs = b"".join([
        attr(name_i, class_i, TYPE_STRING, class_i),
        attr(exported_i, 0xFFFFFFFF, 0x12, 0),
        attr(authorities_i, authority_i, TYPE_STRING, authority_i),
        attr(process_i, game_i, TYPE_STRING, game_i),
    ])
    start = struct.pack(
        "<HHI4I6H",
        RES_XML_START_ELEMENT_TYPE,
        16,
        36 + len(attrs),
        0,
        0xFFFFFFFF,
        0xFFFFFFFF,
        provider_i,
        20,
        20,
        4,
        0,
        0,
        0,
    ) + attrs
    end = struct.pack(
        "<HHI4I",
        RES_XML_END_ELEMENT_TYPE,
        16,
        24,
        0,
        0xFFFFFFFF,
        0xFFFFFFFF,
        provider_i,
    )
    return start, end


def ensure_game_process_provider(axml: bytes) -> bytes:
    """Declare the :game diagnostics provider. Repacking an already patched manifest must keep it."""
    if _has_provider(axml, GAME_PROCESS_PROVIDER):
        return axml
    resource_map = _read_resource_map(axml)
    pool_size = struct.unpack_from("<I", axml, 12)[0]
    existing = _parse_string_pool(axml[8 : 8 + pool_size])
    extra = [
        value
        for value in (GAME_PROCESS_PROVIDER, GAME_PROCESS_AUTHORITY, ":game", "provider")
        if value not in existing
    ]
    pool, strings = _rebuild_string_pool_with_appended(axml[8 : 8 + pool_size], extra)
    for required in ("name", "exported", "authorities", "process", "provider", ":game"):
        if required not in strings:
            raise ValueError(f"Manifest is missing the {required} string needed for the game provider")
    start, end = _provider_element(strings, resource_map)
    chunks = [pool]
    offset = 8 + pool_size
    inserted = False
    while offset < len(axml):
        kind, _, size = struct.unpack_from("<HHI", axml, offset)
        if size < 8 or offset + size > len(axml):
            raise ValueError("Corrupt AXML chunk while inserting the game provider")
        chunk = axml[offset : offset + size]
        chunks.append(chunk)
        if not inserted and kind == RES_XML_START_ELEMENT_TYPE:
            element, _attrs = _get_start_element_info(chunk, strings)
            if element == "application":
                chunks.append(start)
                chunks.append(end)
                inserted = True
        offset += size
    if not inserted:
        raise ValueError("application element not found while inserting the game provider")
    body = b"".join(chunks)
    return struct.pack("<HHI", 3, 8, len(body) + 8) + body


def _android_attr(
    strings: list[str],
    resource_map: list[int],
    name: str,
    raw: int,
    typ: int,
    data: int,
) -> bytes:
    return struct.pack(
        "<IIIHBBI",
        strings.index(ANDROID_NS),
        _mapped_string_index(strings, resource_map, name),
        raw,
        8,
        0,
        typ,
        data,
    )


def _xml_start(name_index: int, attrs: bytes, count: int) -> bytes:
    return struct.pack(
        "<HHI4I6H",
        RES_XML_START_ELEMENT_TYPE,
        16,
        36 + len(attrs),
        0,
        0xFFFFFFFF,
        0xFFFFFFFF,
        name_index,
        20,
        20,
        count,
        0,
        0,
        0,
    ) + attrs


def _xml_end(name_index: int) -> bytes:
    return struct.pack(
        "<HHI4I",
        RES_XML_END_ELEMENT_TYPE,
        16,
        24,
        0,
        0xFFFFFFFF,
        0xFFFFFFFF,
        name_index,
    )


def _generation_service_chunks(strings: list[str], resource_map: list[int]) -> bytes:
    for required in ("service", "property", "name", "exported", "process", "foregroundServiceType", "value", ":game"):
        if required not in strings:
            raise ValueError(f"Manifest is missing the {required} string needed for the generation service")
    service_index = strings.index("service")
    property_index = strings.index("property")
    class_index = strings.index(GAME_SERVICE)
    game_index = strings.index(":game")
    property_name = strings.index(FGS_PROPERTY)
    property_value = strings.index(FGS_SUBTYPE)
    service_attrs = b"".join([
        _android_attr(strings, resource_map, "name", class_index, TYPE_STRING, class_index),
        _android_attr(strings, resource_map, "exported", 0xFFFFFFFF, 0x12, 0),
        _android_attr(strings, resource_map, "process", game_index, TYPE_STRING, game_index),
        _android_attr(strings, resource_map, "foregroundServiceType", 0xFFFFFFFF, 0x11, 0x40000000),
    ])
    property_attrs = b"".join([
        _android_attr(strings, resource_map, "name", property_name, TYPE_STRING, property_name),
        _android_attr(strings, resource_map, "value", property_value, TYPE_STRING, property_value),
    ])
    return b"".join([
        _xml_start(service_index, service_attrs, 4),
        _xml_start(property_index, property_attrs, 2),
        _xml_end(property_index),
        _xml_end(service_index),
    ])


def ensure_generation_service(axml: bytes) -> bytes:
    """Keep :game at foreground-service importance while the first-run generator is alive."""
    if _has_component(axml, "service", GAME_SERVICE):
        return axml
    axml, strings = _with_extra_strings(axml, [GAME_SERVICE, FGS_PROPERTY, FGS_SUBTYPE, ":game"])
    resource_map = _read_resource_map(axml)
    blob = _generation_service_chunks(strings, resource_map)
    pool_size = struct.unpack_from("<I", axml, 12)[0]
    parts: list[bytes] = []
    offset = 8 + pool_size
    inserted = False
    while offset < len(axml):
        kind, _, size = struct.unpack_from("<HHI", axml, offset)
        if size < 8 or offset + size > len(axml):
            raise ValueError("Corrupt AXML chunk while inserting the generation service")
        chunk = axml[offset : offset + size]
        parts.append(chunk)
        if not inserted and kind == RES_XML_START_ELEMENT_TYPE:
            element, _attrs = _get_start_element_info(chunk, strings)
            if element == "application":
                parts.append(blob)
                inserted = True
        offset += size
    if not inserted:
        raise ValueError("application element not found while inserting the generation service")
    body = axml[8 : 8 + pool_size] + b"".join(parts)
    return struct.pack("<HHI", 3, 8, len(body) + 8) + body


def main() -> int:
    if len(sys.argv) not in (3, 5):
        print(
            "Usage: patch_manifest.py <input-manifest> <output-manifest> [version-code version-name]",
            file=sys.stderr,
        )
        return 2
    in_path = Path(sys.argv[1])
    out_path = Path(sys.argv[2])
    version_code = int(sys.argv[3]) if len(sys.argv) == 5 else None
    version_name = sys.argv[4] if len(sys.argv) == 5 else None
    try:
        patched = patch_manifest_bytes(in_path.read_bytes(), version_code, version_name)
        problems = verify_attribute_order(patched)
    except ValueError as exc:
        print(f"patch_manifest.py: {exc}", file=sys.stderr)
        return 1
    if problems:
        print("Attribute order verification failed:", file=sys.stderr)
        for problem in problems:
            print(f"  {problem}", file=sys.stderr)
        return 1
    out_path.write_bytes(patched)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

