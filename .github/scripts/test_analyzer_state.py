"""Stubbed checks for .github/scripts/analyzer_state.py mirror semantics."""

import hashlib
import sys
import tempfile
import unittest
from pathlib import Path

repo_root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(repo_root / ".github" / "scripts"))

import analyzer_state


class FakeClient:
    def __init__(self, objects):
        self.objects = dict(objects)
        self.puts = []
        self.deletes = []

    def get_paginator(self, name):
        assert name == "list_objects_v2"
        client = self

        class Paginator:
            def paginate(self, Bucket):
                contents = [
                    {
                        "Key": key,
                        "Size": len(body),
                        "ETag": '"' + hashlib.md5(body).hexdigest() + '"',
                    }
                    for key, body in sorted(client.objects.items())
                ]
                yield {"Contents": contents}

        return Paginator()

    def get_object(self, Bucket, Key):
        body = self.objects[Key]

        class Body:
            def read(self):
                return body

        return {"Body": Body()}

    def put_object(self, Bucket, Key, Body):
        self.objects[Key] = Body
        self.puts.append((Key, Body))

    def delete_object(self, Bucket, Key):
        del self.objects[Key]
        self.deletes.append(Key)


BUCKET = "test-bucket"


class AnalyzerStateTests(unittest.TestCase):
    def test_excluded_skips_locks_staging_and_duckdb_temp(self):
        for key in (
            ".lock",
            "facts/.lock.lease",
            "facts/hourly/x.parquet.tmp-abc",
            "duckdb-tmp/scratch.db",
        ):
            with self.subTest(key=key):
                self.assertTrue(analyzer_state._excluded(key))

    def test_excluded_keeps_committed_facts(self):
        for key in (
            "facts/hourly/2026-10-20T01.parquet",
            "status.json",
            "logs/run.log",
        ):
            with self.subTest(key=key):
                self.assertFalse(analyzer_state._excluded(key))

    def test_pull_refuses_non_empty_root(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "stale").write_bytes(b"x")
            with self.assertRaises(SystemExit):
                analyzer_state.pull(FakeClient({}), BUCKET, root)

    def test_pull_restores_objects_and_nested_dirs(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "data"
            client = FakeClient(
                {
                    "status.json": b"{}",
                    "facts/hourly/2026-10-20T01.parquet": b"pq",
                    ".lock": b"ignored",
                }
            )
            analyzer_state.pull(client, BUCKET, root)
            self.assertEqual((root / "status.json").read_bytes(), b"{}")
            self.assertEqual(
                (root / "facts" / "hourly" / "2026-10-20T01.parquet").read_bytes(), b"pq"
            )
            self.assertFalse((root / ".lock").exists())

    def test_push_uploads_only_changed_lengths_and_deletes_pruned(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "same.json").write_bytes(b"aa")
            (root / "changed.json").write_bytes(b"new")
            (root / "facts").mkdir()
            (root / "facts" / "d.parquet").write_bytes(b"data")
            (root / ".lock").write_bytes(b"not mirrored")

            client = FakeClient(
                {
                    "same.json": b"aa",
                    "changed.json": b"old",
                    "pruned.json": b"gone",
                    ".lock": b"stale",
                }
            )
            analyzer_state.push(client, BUCKET, root, delete_extra=True)

            uploaded = {key for key, _ in client.puts}
            self.assertNotIn("same.json", uploaded)
            self.assertIn("changed.json", uploaded)
            self.assertIn("facts/d.parquet", uploaded)
            self.assertNotIn(".lock", uploaded)
            self.assertEqual(client.deletes, ["pruned.json"])

    def test_push_new_never_deletes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "a.json").write_bytes(b"a")
            client = FakeClient({"pruned.json": b"gone"})
            analyzer_state.push(client, BUCKET, root, delete_extra=False)
            self.assertEqual(client.deletes, [])
            self.assertIn("pruned.json", client.objects)


if __name__ == "__main__":
    unittest.main()
