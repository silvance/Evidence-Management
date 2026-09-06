#!/usr/bin/env bash
# RELEASE BUNDLE (docs/release-bundle.md) - Linux lane counterpart of New-ReleaseBundle.ps1.
#
# Produces ONE archive that holds everything a deployment needs and nothing it does not:
#   web/      Emc.Web published for the runtime (Emc.Web.exe / Emc.Web, web.config for IIS)
#   worker/   Emc.OcrWorker published for the runtime (the Windows Service executable)
#   db/       schema-v1.sql and its README (applied by the DBA; never by the application)
#   scripts/  deploy/ (worker service scripts) and verify/ (release-bundle verifiers)
#   docs/     the deployment documents and the slice reports
#   release-manifest.json, MANIFEST.sha256
# and beside it <name>.zip and <name>.zip.sha256.
#
# Default is the AIR-GAPPED path: the dependency bundle is verified, restore is LOCKED and reads
# only the bundle (NuGet.Offline.Config, EMC_OFFLINE=true), then build, test, publish, package.
# --connected is a staging dry run over nuget.org (still locked mode); its archive is named
# "-staging-dryrun" and its manifest says so, because it is not a release.
#
# Runtime: win-x64 is the deployment target. linux-x64 exists for this test lane only.
# The build is never self-contained: the server's ASP.NET Core Hosting Bundle (from the
# dependency bundle's prerequisites) supplies the runtime.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
rid="win-x64"; mode="offline"; out_root="$repo/release"; bundle="$repo/dependency-bundle"
skip_tests=0; allow_dirty=0; smoke=0
usage() { sed -n '2,20p' "$0"; echo; echo "usage: $0 [--rid win-x64|linux-x64] [--connected] [--output DIR] [--bundle DIR] [--skip-tests] [--allow-dirty] [--smoke]"; }
while [ $# -gt 0 ]; do
  case "$1" in
    --rid) rid="$2"; shift 2;;
    --connected) mode="connected"; shift;;
    --output) out_root="$2"; shift 2;;
    --bundle) bundle="$2"; shift 2;;
    --skip-tests) skip_tests=1; shift;;
    --allow-dirty) allow_dirty=1; shift;;
    --smoke) smoke=1; shift;;
    -h|--help) usage; exit 0;;
    *) echo "unknown argument: $1" >&2; usage; exit 2;;
  esac
done
case "$rid" in win-x64|linux-x64) ;; *) echo "runtime must be win-x64 (deployment) or linux-x64 (test lane); got $rid" >&2; exit 2;; esac
grep -q "<RuntimeIdentifiers>[^<]*$rid" "$repo/Directory.Build.props" || { echo "$rid is not pinned in Directory.Build.props RuntimeIdentifiers" >&2; exit 2; }

cd "$repo"
sha_full="$(git rev-parse HEAD)"; sha="$(git rev-parse --short HEAD)"; branch="$(git rev-parse --abbrev-ref HEAD)"
dirty=0; [ -z "$(git status --porcelain)" ] || dirty=1
if [ $dirty -eq 1 ] && [ $allow_dirty -eq 0 ]; then
  echo "The working tree has uncommitted changes. A release is built from a commit. Commit, or pass --allow-dirty for a local trial (the manifest will say dirty=true)." >&2; exit 1
fi
version="$(sed -n 's/.*<VersionPrefix>\([^<]*\)<\/VersionPrefix>.*/\1/p' Directory.Build.props | head -1)"
[ -n "$version" ] || { echo "No <VersionPrefix> in Directory.Build.props" >&2; exit 1; }
pinned="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' global.json | head -1)"
installed="$(dotnet --version)"
[ "$installed" = "$pinned" ] || { echo "Installed SDK $installed is not the pinned $pinned (rollForward is disabled)." >&2; exit 1; }

name="emc-$version-$rid-$sha"; [ "$mode" = "connected" ] && name="$name-staging-dryrun"
stage="$out_root/$name"; zipfile="$out_root/$name.zip"
rm -rf "$stage" "$zipfile" "$zipfile.sha256"; mkdir -p "$stage"
log="$out_root/$name.build.log"; : > "$log"
echo "Release bundle $name ($mode, SDK $pinned, commit $sha_full on $branch)"

# 1. Restore - locked, and offline unless this is a staging dry run.
bundle_manifest_sha=""; bundle_audit=""
if [ "$mode" = "offline" ]; then
  "$repo/scripts/airgap/verify-dependency-bundle.sh" "$bundle"
  bundle_manifest_sha="$(sha256sum "$bundle/manifest.json" | cut -d' ' -f1)"
  bundle_audit="$(grep -o '"auditDateUtc": *"[^"]*"' "$bundle/manifest.json" | head -1 | sed 's/.*: *"\(.*\)"/\1/')"
  cfg="NuGet.Offline.Config"; props="-p:EMC_OFFLINE=true"
  echo "Restore (offline, locked, source: $bundle/packages)"
  # NuGet.Offline.Config names the repository's own dependency-bundle/packages; --source makes
  # the SAME folder the only source when --bundle points elsewhere. Nothing else is consulted.
  dotnet restore Emc.sln --configfile "$cfg" --source "$bundle/packages" --locked-mode $props >>"$log" 2>&1 || { tail -30 "$log" >&2; echo "Offline restore failed: something the lock files name is not in the bundle (apphost packs for $rid included). Nothing outside the bundle was consulted." >&2; exit 1; }
else
  cfg="NuGet.Config"; props=""
  echo "Restore (connected STAGING DRY RUN, locked, nuget.org)"
  dotnet restore Emc.sln --configfile "$cfg" --locked-mode >>"$log" 2>&1 || { tail -30 "$log" >&2; exit 1; }
fi

# 2. Build and test, Release configuration, nothing restored from here on.
echo "Build (Release)"
dotnet build Emc.sln --no-restore -c Release $props >>"$log" 2>&1 || { tail -40 "$log" >&2; exit 1; }
tests_ran=false; passed=0; failed=0; skipped=0; sql_lane="skipped (EMC_SQLSERVER_TEST_CONNECTION not set)"
[ -n "${EMC_SQLSERVER_TEST_CONNECTION:-}" ] && sql_lane="ran"
if [ $skip_tests -eq 0 ]; then
  echo "Test (Release, no build)"
  trx="$out_root/$name.tests"; rm -rf "$trx"; mkdir -p "$trx"
  if ! dotnet test Emc.sln --no-build -c Release $props --logger "trx" --results-directory "$trx" >>"$log" 2>&1; then
    grep -E "Failed |Failed!|error" "$log" | tail -20 >&2; echo "Tests failed. No bundle is produced from a failing tree." >&2; exit 1
  fi
  tests_ran=true
  for f in "$trx"/*.trx; do
    c="$(grep -o '<Counters [^/]*/>' "$f" | head -1)"
    passed=$((passed + $(echo "$c" | sed -n 's/.* passed="\([0-9]*\)".*/\1/p')))
    failed=$((failed + $(echo "$c" | sed -n 's/.* failed="\([0-9]*\)".*/\1/p')))
    total="$(echo "$c" | sed -n 's/.* total="\([0-9]*\)".*/\1/p')"; executed="$(echo "$c" | sed -n 's/.* executed="\([0-9]*\)".*/\1/p')"
    skipped=$((skipped + total - executed))
  done
  echo "Tests: $passed passed, $failed failed, $skipped skipped (SQL Server lane: $sql_lane)"
  [ "$failed" -eq 0 ] || exit 1
else
  echo "Tests SKIPPED on request: the manifest will say so; this is not a release."
fi

# 3. Publish the two executables for the runtime. Framework-dependent; the commit is stamped
#    into the informational version.
for proj in Emc.Web:web Emc.OcrWorker:worker; do
  p="${proj%%:*}"; d="${proj##*:}"
  echo "Publish $p ($rid) -> $d/"
  dotnet publish "src/$p" --no-restore -c Release -r "$rid" --self-contained false $props -p:SourceRevisionId="$sha" -o "$stage/$d" >>"$log" 2>&1 || { tail -40 "$log" >&2; exit 1; }
done
# Nothing that is development-only or could hold a secret leaves with the bundle.
rm -f "$stage/web/appsettings.Development.json" "$stage/worker/appsettings.Development.json"
find "$stage" \( -name 'appsettings.*.Local.json' -o -name 'appsettings.Local.json' -o -name 'secrets.json' -o -name '*.pfx' -o -name '*.key' -o -name '*.db' \) -print -delete | sed 's/^/removed: /'
if grep -liE 'password=|pwd=|user id=|accesskey|secret' "$stage/web/appsettings.json" "$stage/worker/appsettings.json" $( [ -f "$stage/web/web.config" ] && echo "$stage/web/web.config" ); then
  echo "A published configuration file carries a credential-like value. Not packaged." >&2; exit 1
fi
if [ "$rid" = "win-x64" ]; then web_exe="web/Emc.Web.exe"; worker_exe="worker/Emc.OcrWorker.exe"; else web_exe="web/Emc.Web"; worker_exe="worker/Emc.OcrWorker"; fi
# The IIS web.config is emitted by the publish for Windows runtimes only; the test lane has none.
webconfig=""; [ "$rid" = "win-x64" ] && webconfig="web/web.config"
for e in "$web_exe" "$worker_exe" $webconfig web/appsettings.json worker/appsettings.json web/Emc.Web.dll worker/Emc.OcrWorker.dll; do
  [ -f "$stage/$e" ] || { echo "Expected publish output missing: $e" >&2; exit 1; }
done

# 4. Everything else the deployment needs, from the same commit.
mkdir -p "$stage/db" "$stage/scripts/deploy" "$stage/scripts/verify" "$stage/docs/slice-reports"
cp db/schema-v1.sql db/README.md "$stage/db/"
cp scripts/deploy/*.ps1 "$stage/scripts/deploy/"
cp scripts/release/Verify-ReleaseBundle.ps1 scripts/release/verify-release-bundle.sh "$stage/scripts/verify/"
cp docs/release-bundle.md docs/ocr-worker-deployment.md docs/air-gapped-build-and-maintenance.md docs/dependency-advisories.md "$stage/docs/"
cp docs/slice-reports/*.md "$stage/docs/slice-reports/"
if [ $smoke -eq 1 ] && [ "$rid" = "linux-x64" ]; then
  echo "Smoke: the published worker renders a page; the published web host starts and answers."
  smoke_dir="$out_root/$name.smoke"; rm -rf "$smoke_dir"; mkdir -p "$smoke_dir"
  printf '%%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\nxref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \ntrailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n186\n%%%%EOF\n' > "$smoke_dir/blank.pdf"
  "$stage/$worker_exe" render info --input "$smoke_dir/blank.pdf" --output "$smoke_dir/info.json"
  grep -q '"PageCount":1' "$smoke_dir/info.json" || { echo "worker render info did not report one page" >&2; exit 1; }
  "$stage/$worker_exe" render page --page 1 --dpi 50 --input "$smoke_dir/blank.pdf" --output "$smoke_dir/p1.png"
  [ "$(head -c 4 "$smoke_dir/p1.png" | od -An -c | tr -d ' ')" = "211PNG" ] || { echo "worker render page did not write a PNG" >&2; exit 1; }
  ( cd "$stage/web" && exec env ASPNETCORE_ENVIRONMENT=Production "./Emc.Web" --urls http://127.0.0.1:5089 ) >"$smoke_dir/web.log" 2>&1 &
  web_pid=$!
  status=""; for _ in $(seq 1 40); do sleep 0.5; status="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5089/ || true)"; [ -n "$status" ] && [ "$status" != "000" ] && break; done
  kill "$web_pid" 2>/dev/null || true; wait "$web_pid" 2>/dev/null || true
  [ -n "$status" ] && [ "$status" != "000" ] || { echo "the published web host did not answer on 127.0.0.1:5089" >&2; tail -20 "$smoke_dir/web.log" >&2; exit 1; }
  echo "Smoke: worker rendered 1 page; web host answered HTTP $status (no database is configured here, so a non-200 is expected)."
elif [ $smoke -eq 1 ]; then
  echo "Smoke requested but the $rid executables cannot run on this host; skipped."
fi

# 5. Manifests: what this is, what it was built from, what was proved, and every file's hash.
built="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
{
  echo '{'
  echo '  "schema": "emc-release-bundle/1",'
  echo '  "product": "EMC (Evidence Management Companion)",'
  echo "  \"version\": \"$version\","
  echo "  \"informationalVersion\": \"$version+$sha\","
  echo "  \"commit\": \"$sha_full\","
  echo "  \"branch\": \"$branch\","
  echo "  \"dirtyWorkingTree\": $([ $dirty -eq 1 ] && echo true || echo false),"
  echo "  \"builtUtc\": \"$built\","
  echo "  \"buildHost\": \"$(uname -s) $(uname -m)\","
  echo "  \"sdk\": { \"version\": \"$pinned\", \"rollForward\": \"disable\" },"
  echo "  \"runtime\": { \"identifier\": \"$rid\", \"purpose\": \"$([ "$rid" = "win-x64" ] && echo 'deployment target' || echo 'test lane only; not a deployment target')\", \"selfContained\": false, \"requires\": \"ASP.NET Core Hosting Bundle for the pinned runtime, from the dependency bundle prerequisites\" },"
  echo '  "configuration": "Release",'
  if [ "$mode" = "offline" ]; then
    echo "  \"restore\": { \"mode\": \"offline-locked\", \"source\": \"dependency bundle\", \"dependencyBundleManifestSha256\": \"$bundle_manifest_sha\", \"dependencyAuditDateUtc\": \"$bundle_audit\" },"
  else
    echo '  "restore": { "mode": "connected-locked", "source": "nuget.org (STAGING DRY RUN - NOT A RELEASE)", "dependencyBundleManifestSha256": null, "dependencyAuditDateUtc": null },'
  fi
  echo "  \"tests\": { \"ran\": $tests_ran, \"passed\": $passed, \"failed\": $failed, \"skipped\": $skipped, \"sqlServerLane\": \"$sql_lane\" },"
  echo '  "components": ['
  echo "    { \"name\": \"Emc.Web\", \"path\": \"web\", \"entryPoint\": \"$web_exe\", \"hosting\": \"$([ "$rid" = "win-x64" ] && echo 'IIS in-process (web/web.config)' || echo 'Kestrel (test lane; no IIS web.config)'); Windows Authentication; never migrates the database (AUD-012)\" },"
  echo "    { \"name\": \"Emc.OcrWorker\", \"path\": \"worker\", \"entryPoint\": \"$worker_exe\", \"hosting\": \"Windows Service EmcOcrWorker via scripts/deploy/Install-EmcOcrWorker.ps1; also the render child process\" }"
  echo '  ],'
  echo '  "database": { "schemaScript": "db/schema-v1.sql", "appliedBy": "the DBA, from the script, before the first start; the application holds no DDL rights" },'
  echo '  "files": ['
  first=1
  while IFS= read -r f; do
    rel="${f#$stage/}"; h="$(sha256sum "$f" | cut -d' ' -f1)"; b="$(stat -c %s "$f")"
    [ $first -eq 1 ] || echo ','; first=0
    printf '    { "path": "%s", "sha256": "%s", "bytes": %s }' "$rel" "$h" "$b"
  done < <(find "$stage" -type f ! -name release-manifest.json ! -name MANIFEST.sha256 | LC_ALL=C sort)
  echo; echo '  ]'
  echo '}'
} > "$stage/release-manifest.json"
( cd "$stage" && find . -type f ! -name MANIFEST.sha256 | LC_ALL=C sort | sed 's|^\./||' | xargs sha256sum > MANIFEST.sha256 )

# 6. One archive, and its hash beside it.
( cd "$out_root" && rm -f "$name.zip" && zip -q -r -X "$name.zip" "$name" )
( cd "$out_root" && sha256sum "$name.zip" > "$name.zip.sha256" )
"$repo/scripts/release/verify-release-bundle.sh" "$stage" >/dev/null
echo "Release bundle: $zipfile"
echo "SHA-256:        $(cut -d' ' -f1 "$zipfile.sha256")"
echo "Verify on the receiving side with scripts/verify/verify-release-bundle.sh (or Verify-ReleaseBundle.ps1) from inside the archive."
