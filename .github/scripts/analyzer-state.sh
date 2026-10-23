#!/usr/bin/env bash
# Mirrors the analyzer Data Root (BPP_DATA_ROOT) to and from its private R2
# state bucket for .github/workflows/analyzer.yml, which runs on a fresh runner
# every time. Hourly commits and day seals are immutable and carry their own
# sha256 (bazaarplusplus-analyzer/docs/architecture.md), so `bpp verify --deep`
# after a pull proves the restore. Lock and staging entries are never mirrored.
#
#   analyzer-state.sh pull       copy the bucket into an empty Data Root
#   analyzer-state.sh push       mirror the Data Root, deleting what retention
#                                pruned; only after a successful run
#   analyzer-state.sh push-new   upload new and changed files, delete nothing;
#                                after a failed run
#
# The mirror runs on the analyzer's locked boto3 dependency
# (.github/scripts/analyzer_state.py). It must not shell out to the AWS CLI:
# ubuntu-latest moved to Ubuntu 26 on 2026-10-19 and the runner no longer ships
# `aws`, which failed every production run at `Restore the Data Root`.
#
# Reads BPP_DATA_ROOT, BPP_ANALYZER_STATE_BUCKET, CLOUDFLARE_ACCOUNT_ID and
# BPP_ANALYZER_R2_TOKEN.
set -euo pipefail

: "${BPP_DATA_ROOT:?}" "${BPP_ANALYZER_STATE_BUCKET:?}" "${CLOUDFLARE_ACCOUNT_ID:?}"
: "${BPP_ANALYZER_R2_TOKEN:?}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
analyzer_root="${repo_root}/bazaarplusplus-analyzer"

cd "${analyzer_root}"
uv run --locked --no-dev python "${repo_root}/.github/scripts/analyzer_state.py" "$1" \
  --data-root "${BPP_DATA_ROOT}" \
  --bucket "${BPP_ANALYZER_STATE_BUCKET}" \
  --account-id "${CLOUDFLARE_ACCOUNT_ID}" \
  --api-token "${BPP_ANALYZER_R2_TOKEN}"
