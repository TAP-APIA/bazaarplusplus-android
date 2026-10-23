#!/usr/bin/env python3
"""Mirror the analyzer Data Root (BPP_DATA_ROOT) to and from its private R2 bucket.

Replaces ``aws s3 sync`` in ``.github/scripts/analyzer-state.sh``. The workflow
never installed the AWS CLI, so the restore step depended on whatever the runner
image happened to ship; when ubuntu-latest migrated on 2026-10-19 that dependency
disappeared and every run failed at ``Restore the Data Root``. boto3 is already a
locked analyzer dependency, so mirroring through it removes the ambient-CLI
requirement entirely.

Semantics match the previous ``aws s3 sync`` invocations:
  pull       copy the bucket into an empty Data Root
  push       mirror the Data Root, deleting what retention pruned
  push-new   upload new and changed files, delete nothing

Hourly commits and day seals are immutable and carry their own sha256
(bazaarplusplus-analyzer/docs/architecture.md), so ``bpp verify --deep`` after a
pull proves the restore. Lock and staging entries are never mirrored.
"""

from __future__ import annotations

import argparse
import hashlib
import sys
from pathlib import Path, PurePosixPath


def _excluded(key: str) -> bool:
    """DirectoryLock, durable.atomic_replace and FactStore staging names."""
    parts = PurePosixPath(key).parts
    if not parts:
        return True
    if parts[0] == "duckdb-tmp":
        return True
    return any(part.startswith(".lock") or ".tmp-" in part for part in parts)


def _local_files(root: Path) -> dict[str, Path]:
    files: dict[str, Path] = {}
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        key = path.relative_to(root).as_posix()
        if _excluded(key):
            continue
        files[key] = path
    return files


def _remote_entries(client, bucket: str) -> dict[str, tuple[int, str | None]]:
    """Size and MD5 ETag per key; multipart ETags (``...-N``) are not MD5s."""
    entries: dict[str, tuple[int, str | None]] = {}
    paginator = client.get_paginator("list_objects_v2")
    for page in paginator.paginate(Bucket=bucket):
        for entry in page.get("Contents", []):
            key = entry["Key"]
            if _excluded(key):
                continue
            etag = str(entry.get("ETag", "")).strip('"') or None
            entries[key] = (int(entry["Size"]), None if etag and "-" in etag else etag)
    return entries


def _md5(path: Path) -> str:
    digest = hashlib.md5()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def pull(client, bucket: str, root: Path) -> None:
    if root.exists() and any(root.iterdir()):
        raise SystemExit(f"::error::{root} must be empty before a pull")
    root.mkdir(parents=True, exist_ok=True)
    keys = sorted(_remote_entries(client, bucket))
    for index, key in enumerate(keys, start=1):
        body = client.get_object(Bucket=bucket, Key=key)["Body"].read()
        target = root / key
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(body)
        if index % 500 == 0:
            print(f"pulled {index}/{len(keys)} objects", flush=True)
    print(f"pulled {len(keys)} objects into {root}", flush=True)


def push(client, bucket: str, root: Path, *, delete_extra: bool) -> None:
    local = _local_files(root)
    remote = _remote_entries(client, bucket)

    uploaded = 0
    for key, path in sorted(local.items()):
        size = path.stat().st_size
        remote_size, remote_md5 = remote.get(key, (-1, None))
        # Compare content, not just length: a same-size rewrite must still upload.
        # A multipart ETag cannot prove equality, so it falls back to uploading.
        unchanged = remote_size == size and remote_md5 == _md5(path)
        if not unchanged:
            client.put_object(Bucket=bucket, Key=key, Body=path.read_bytes())
            uploaded += 1
    print(f"uploaded {uploaded} of {len(local)} objects", flush=True)

    if not delete_extra:
        return
    # Only a fully successful run mirrors deletions; retention already pruned locally.
    deleted = 0
    for key in sorted(set(remote) - set(local)):
        client.delete_object(Bucket=bucket, Key=key)
        deleted += 1
    print(f"deleted {deleted} objects removed by retention", flush=True)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("pull", "push", "push-new"))
    parser.add_argument("--data-root", required=True)
    parser.add_argument("--bucket", required=True)
    parser.add_argument("--account-id", required=True)
    parser.add_argument("--api-token", required=True)
    args = parser.parse_args(argv)

    # Imported here so the module can be imported (and unit-tested) without the
    # analyzer's locked dependency set installed.
    analyzer_src = Path(__file__).resolve().parents[2] / "bazaarplusplus-analyzer" / "src"
    sys.path.insert(0, str(analyzer_src))
    from bppanalyzer.object_store import R2ObjectStore

    store = R2ObjectStore(
        account_id=args.account_id,
        bucket=args.bucket,
        api_token=args.api_token,
    )
    root = Path(args.data_root)
    client = store._client

    if args.command == "pull":
        pull(client, store.bucket, root)
    elif args.command == "push":
        push(client, store.bucket, root, delete_extra=True)
    else:
        push(client, store.bucket, root, delete_extra=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
