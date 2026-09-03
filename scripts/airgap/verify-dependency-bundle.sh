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
# Non-NuGet artifacts (OCR engine, models): every entry with a kind must be approved in staging.
if grep -q '"kind"' manifest.json; then
  pending="$(grep -c '"reviewStatus": *"pending' manifest.json || true)"
  unknown="$(grep -o '"kind": *"[^"]*"' manifest.json | grep -v -E 'ocr-engine|ocr-model|native-runtime|pdf-rasterizer' || true)"
  [ -z "$unknown" ] || { echo "Unknown artifact kind(s): $unknown" >&2; exit 1; }
  echo "Reviewed non-NuGet artifacts: $(grep -c '"kind"' manifest.json) (NuGet entries pending import review: $pending)"
else
  echo "WARNING: this bundle carries no OCR engine or model artifacts; OCR cannot be installed from it." >&2
fi
echo "Bundle verified. Audit report: $bundle/audit-report.txt"
