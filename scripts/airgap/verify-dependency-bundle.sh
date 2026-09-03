#!/usr/bin/env bash
# AIR-GAPPED: verify the dependency bundle's hashes with coreutils only. No network.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
bundle="${1:-$repo/dependency-bundle}"
cd "$bundle"
[ -f MANIFEST.sha256 ] || { echo "MANIFEST.sha256 not found in $bundle" >&2; exit 1; }
sha256sum --check --strict MANIFEST.sha256
pinned="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$repo/global.json" | head -1)"
built="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' manifest.json | head -1)"
[ "$pinned" = "$built" ] || { echo "Bundle built for SDK $built; repository pins $pinned" >&2; exit 1; }
echo "Bundle verified. Audit report: $bundle/audit-report.txt"
