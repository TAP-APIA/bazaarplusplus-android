import struct
import sys
from pathlib import Path

RES_STRING_POOL_TYPE = 0x0001
RES_XML_START_ELEMENT_TYPE = 0x0102
RES_XML_END_ELEMENT_TYPE = 0x0103
TYPE_STRING = 0x03

BPP_ACTIVITY_NAME = "com.bepinex.android.bpp.BppLauncherActivity"
ORIG_MAIN_ACTIVITY_NAME = "com.bepinex.android.MainActivity"
BPP_APP_LABEL = "BPP Android"


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
) -> bytes:
    if len(axml) < 16 or struct.unpack_from("<I", axml, 0)[0] != 0x00080003:
        raise ValueError("Invalid binary AndroidManifest.xml header")

    pool_type, pool_header, pool_size = struct.unpack_from("<HHI", axml, 8)
    if pool_type != RES_STRING_POOL_TYPE:
        raise ValueError("First chunk in AndroidManifest.xml is not a string pool")

    existing_strings = _parse_string_pool(axml[8 : 8 + pool_size])
    if BPP_ACTIVITY_NAME in existing_strings:
        raise ValueError("Manifest is already patched with BppLauncherActivity")
    if version_code is not None and not (0 < version_code < 0x80000000):
        raise ValueError(f"Invalid versionCode: {version_code}")
    if version_name is not None and not version_name.strip():
        raise ValueError("versionName must not be empty")

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
    return header + body


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
    patched = patch_manifest_bytes(in_path.read_bytes(), version_code, version_name)
    out_path.write_bytes(patched)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
