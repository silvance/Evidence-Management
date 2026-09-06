#!/usr/bin/env bash
# RELEASE BUNDLE: verify an EMC release bundle (a folder, or a .zip which is unpacked to a
# temporary folder) before anything in it is deployed. coreutils only; no network.
#   - every file matches MANIFEST.sha256 and release-manifest.json (both, so they cannot disagree)
#   - the entry points, the IIS web.config, the schema script and the deploy scripts are present
#   - the tests ran and none failed; a staging dry run or a dirty tree is called out
#   - no published configuration file carries a credential-like value
set -euo pipefail
target="${1:-.}"
tmp=""
if [ -f "$target" ] && [[ "$target" == *.zip ]]; then
  [ ! -f "$target.sha256" ] || (cd "$(dirname "$target")" && sha256sum --check --strict "$(basename "$target").sha256" >/dev/null && echo "archive hash matches $(basename "$target").sha256")
  tmp="$(mktemp -d)"; unzip -q "$target" -d "$tmp"; target="$(find "$tmp" -mindepth 1 -maxdepth 1 -type d | head -1)"
fi
cd "$target"
[ -f MANIFEST.sha256 ] && [ -f release-manifest.json ] || { echo "not a release bundle: MANIFEST.sha256 or release-manifest.json missing in $target" >&2; exit 1; }
sha256sum --check --strict --quiet MANIFEST.sha256 && echo "MANIFEST.sha256: $(wc -l < MANIFEST.sha256) files match"
listed="$(grep -c '"bytes": ' release-manifest.json)"; onDisk="$(find . -type f ! -name MANIFEST.sha256 ! -name release-manifest.json | wc -l)"
[ "$listed" = "$onDisk" ] || { echo "release-manifest.json lists $listed files; the bundle holds $onDisk" >&2; exit 1; }
python3 - <<'PY' 2>/dev/null || {
import hashlib, json
m = json.load(open('release-manifest.json'))
for f in m['files']:
    h = hashlib.sha256(open(f['path'], 'rb').read()).hexdigest()
    assert h == f['sha256'], f"release-manifest.json hash mismatch: {f['path']}"
print(f"release-manifest.json: {len(m['files'])} files match")
PY
  # Without python3, cross-check the manifest's hashes against the plain list instead.
  while IFS= read -r line; do
    p="$(echo "$line" | sed -n 's/.*"path": "\([^"]*\)".*/\1/p')"; h="$(echo "$line" | sed -n 's/.*"sha256": "\([^"]*\)".*/\1/p')"
    grep -q "^$h  $p\$" MANIFEST.sha256 || { echo "release-manifest.json disagrees with MANIFEST.sha256 for $p" >&2; exit 1; }
  done < <(grep '"path": ' release-manifest.json)
  echo "release-manifest.json: $listed files agree with MANIFEST.sha256"
}
field() { grep -o "\"$1\": *\"[^\"]*\"" release-manifest.json | head -1 | sed 's/.*: *"\(.*\)"/\1/'; }
schema="$(field schema)"; [ "$schema" = "emc-release-bundle/1" ] || { echo "unsupported schema $schema" >&2; exit 1; }
for e in $(grep -o '"entryPoint": *"[^"]*"' release-manifest.json | sed 's/.*: *"\(.*\)"/\1/'); do [ -f "$e" ] || { echo "entry point missing: $e" >&2; exit 1; }; done
rid="$(field identifier)"
for f in web/appsettings.json worker/appsettings.json db/schema-v1.sql scripts/deploy/Install-EmcOcrWorker.ps1 scripts/deploy/Set-EmcOcrWorkerConfig.ps1 docs/release-bundle.md; do [ -f "$f" ] || { echo "required file missing: $f" >&2; exit 1; }; done
if [ "$rid" = "win-x64" ]; then
  [ -f web/web.config ] || { echo "required file missing: web/web.config" >&2; exit 1; }
  grep -q 'aspNetCore processPath=' web/web.config || { echo "web/web.config carries no aspNetCore handler: not a published IIS site" >&2; exit 1; }
else
  echo "WARNING: runtime $rid is the test lane, not a deployment target." >&2
fi
! grep -liE 'password=|pwd=|user id=|accesskey|secret' web/appsettings.json worker/appsettings.json $( [ -f web/web.config ] && echo web/web.config ) >/dev/null || { echo "a published configuration file carries a credential-like value" >&2; exit 1; }
! find . -name 'appsettings.Development.json' -o -name '*.Local.json' -o -name 'secrets.json' -o -name '*.pfx' -o -name '*.key' | grep -q . || { echo "development-only or secret-bearing files are in the bundle" >&2; exit 1; }
failed="$(grep -o '"failed": *[0-9]*' release-manifest.json | head -1 | grep -o '[0-9]*$')"; ran="$(grep -o '"ran": *[a-z]*' release-manifest.json | head -1 | grep -o '[a-z]*$')"
[ "${failed:-0}" = "0" ] || { echo "the manifest records $failed failed test(s): not a release" >&2; exit 1; }
[ "$ran" = "true" ] || echo "WARNING: tests were not run for this bundle (tests.ran=false). It is not a release." >&2
grep -q '"dirtyWorkingTree": true' release-manifest.json && echo "WARNING: built from a dirty working tree; the commit does not describe it. Not a release." >&2
grep -q 'STAGING DRY RUN' release-manifest.json && echo "WARNING: staging dry run restored from nuget.org, not from the verified dependency bundle. Not a release." >&2
echo "Release bundle OK: $(field product) $(field informationalVersion), runtime $(field identifier), built $(field builtUtc) from commit $(field commit)"
echo "Tests: $(grep -o '"tests": {[^}]*}' release-manifest.json)"
[ -z "$tmp" ] || rm -rf "$tmp"
