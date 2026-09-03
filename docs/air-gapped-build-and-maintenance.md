# Air-gapped build and maintenance

**[CONTROL] — a design constraint of this program, not an AR 195-5 requirement.**

EMC is developed, restored, built, tested, deployed, operated, patched and maintained inside an
**air-gapped environment with no Internet connectivity**. Internet-connected systems are used only
as a **controlled staging environment** to research dependencies, obtain approved packages and
installers, audit them, verify hashes, and produce a bundle that crosses into the enclave by the
organization's approved media-transfer process.

Nothing at run time depends on public NuGet, GitHub, a CDN, external fonts, a cloud API, a
telemetry endpoint, licence validation, cloud identity, package downloads, remote OCR, or a
vulnerability feed. Windows Authentication uses the enclave's own domain infrastructure; SQL
Server is local. A test enforces the parts of this that can be checked from source
(`OfflineBuildTests`).

## The two environments

| | Connected staging | Air-gapped development / build / deployment |
|---|---|---|
| Purpose | Dependency research; download; **vulnerability audit**; signature and hash validation; bundle export | Offline restore; build; tests; deployment; operation; maintenance |
| Network | Internet, as policy permits | **None** |
| Evidence data | **Never.** No EMC database, no DA Form 4137, no case data | The only place EMC data exists |
| NuGet configuration | `NuGet.Config` — nuget.org only, inherited sources cleared | `NuGet.Offline.Config` — the bundle folder only, inherited sources cleared, audit sources cleared |
| Scripts | `scripts/staging/Export-DependencyBundle.ps1` (+ `artifacts.manifest.example.json`) | `scripts/airgap/Verify-DependencyBundle.ps1`, `verify-dependency-bundle.sh`, `Restore-Build-Test-Offline.ps1` |

## What is pinned, and why

- **SDK.** `global.json` pins the exact SDK (`10.0.111`) with `"rollForward": "disable"`. A
  different SDK on the air-gapped host is a build failure, not a silent substitution. The bundle
  carries the SDK installer/archive; the deployment bundle carries the ASP.NET Core **Hosting
  Bundle** for the same runtime, because the application is framework-dependent and neither
  Windows Update nor dotnet.microsoft.com is reachable.
- **Packages.** `Directory.Build.props` sets `RestorePackagesWithLockFile=true`; every project's
  `packages.lock.json` is committed. The lock files are the reviewed, exact dependency graph —
  direct and transitive. **An unexplained lock-file change is a dependency change** and is
  reviewed as one. No floating versions.
- **Restore mode.** The offline restore runs `--locked-mode`, so restore fails rather than resolve
  anything the lock files do not name. Continuous-integration builds in staging also use locked
  mode (`ContinuousIntegrationBuild=true`).

## Producing a bundle (connected staging)

```
pwsh scripts/staging/Export-DependencyBundle.ps1 `
    -SdkInstaller <path to dotnet-sdk-10.0.111-win-x64.exe> `
    -HostingBundleInstaller <path to dotnet-hosting-10.0.11-win.exe>
```

The script:

1. restores every project against **nuget.org only** and regenerates lock files
   (`--force-evaluate`);
2. runs the **vulnerability audit** — direct and transitive, every severity — and **fails on any
   finding**. The report is written to the bundle as `audit-report.txt`. A finding is resolved, or
   assessed and recorded in `docs/dependency-advisories.md`, before an export succeeds;
3. copies **exactly the packages the lock files name** out of the global packages folder — never
   the whole cache;
4. copies the SDK and Hosting Bundle installers, when supplied;
5. writes `manifest.json` (name, version, file, SHA-256, origin, retrieval date, licence where
   declared, classification runtime / build-only / test-only, audit status and date, review
   status) and `MANIFEST.sha256` (a plain `sha256sum -c` list of every file).

The installers' own hashes should additionally be checked against Microsoft's published checksums
in staging; record where they were obtained in the manifest's `reviewStatus` at import review.

## Importing a bundle (air-gapped)

1. Transfer `dependency-bundle/` by the approved media process.
2. **Verify before use:** `pwsh scripts/airgap/Verify-DependencyBundle.ps1` or
   `scripts/airgap/verify-dependency-bundle.sh`. Every file is hashed against `MANIFEST.sha256`
   and cross-checked against `manifest.json`; the bundle's SDK version is compared to
   `global.json`. Any mismatch stops the import.
3. Install the SDK from `prerequisites/` if the pinned version is not present.
4. Review `manifest.json` and `audit-report.txt`; mark the import review in your local records.

## Building offline

```
pwsh scripts/airgap/Restore-Build-Test-Offline.ps1
```

which verifies the bundle, checks the installed SDK equals the pinned one, then runs

```
dotnet restore Emc.sln --configfile NuGet.Offline.Config --locked-mode -p:EMC_OFFLINE=true
dotnet build   Emc.sln --no-restore -c Release -p:EMC_OFFLINE=true
dotnet test    Emc.sln --no-build   -c Release -p:EMC_OFFLINE=true
```

`NuGet.Offline.Config` clears every inherited package source and names only
`dependency-bundle/packages`, so nothing can fall back to nuget.org or to a feed configured
elsewhere on the machine. It contains no credentials and never will.

## Vulnerability auditing — stated precisely

NuGet Audit takes its vulnerability data from an **audit source**. A folder of `.nupkg` files is
not one. So:

- The audit **happens in connected staging**, at export, against current data, and fails the
  export on any finding. Its report travels with the bundle.
- The offline build sets `EMC_OFFLINE=true`, which turns `NuGetAudit` off **for that build only**,
  because the offline host has no audit source and a warning about that (NU1905) would otherwise
  fail the build under `TreatWarningsAsErrors`. This is conditional and documented here; it is not
  a global suppression. The offline restore is in locked mode, so it restores **only the packages
  the staging audit covered**.
- The offline build script prints the audit report's date. **An offline build is never a
  statement that packages were checked against current data on that host.**
- If the organization later mirrors approved vulnerability data into the enclave, point
  `auditSources` in `NuGet.Offline.Config` at it and remove the `EMC_OFFLINE` audit condition.

`docs/dependency-advisories.md` is the risk register: every finding is resolved or assessed there
before a bundle is exported.

## Release validation — the SQL Server lane

The domain and SQLite suites run anywhere. They cannot exercise SQL Server-specific controls: the
append-only triggers, filtered indexes as deployed, SQL Server constraint and concurrency
behaviour, or the migrations themselves. The **SQL Server release-validation lane** does, and it
runs **completely offline** against an approved local instance (Developer, Express, or the
enclave's SQL Server):

```
$env:EMC_SQLSERVER_TEST_CONNECTION = "Server=<approved local instance>;Integrated Security=true;TrustServerCertificate=true"
dotnet test tests/Emc.Application.Tests --filter "FullyQualifiedName~SqlServer"
```

It creates a database named `EmcTest_<guid>` from the committed migrations, proves the triggers
exist and reject `UPDATE`/`DELETE` — on common and on table-per-hierarchy subtype columns — on
`ItemEvents`, `AuditEvents`, `OfficialDocumentNumberAssignments` and `VoucherReviewActions`;
proves canonical document-number uniqueness and the one-open-appointment index at the database;
proves concurrency-stamp conflicts; proves `datetimeoffset` round-trips; runs the vertical slice;
and drops the database. Without the variable the lane is **skipped, visibly**. No real evidence
data is ever placed in it. Docker, a container registry, and GitHub Actions are **not**
prerequisites for this; the GitHub workflow that also runs the lane is staging-side convenience
only.

**Not yet executed here.** The lane was written and compiles in this repository's development
environment, which has no SQL Server; it has not been run against a real instance as of this
document's commit. Running it is a release gate, not an option.

## Adding or changing a dependency — the rule

1. Justify it. Prefer the framework or the standard library; every package is something the
   enclave must import, audit and maintain for the life of the system.
2. Add it with an exact version in the `.csproj`. Never a floating version.
3. In **connected staging**: `dotnet restore --force-evaluate`, review the resulting
   `packages.lock.json` diff — every transitive it pulls in is now yours — and run the audit.
4. Commit the `.csproj` and lock-file changes together, with the justification in the commit.
5. Export a new bundle. The air-gapped build will refuse the old one (locked mode).
6. Record any advisory assessment in `docs/dependency-advisories.md`.

Anything that would need a network at **run time** — a CDN script, a web font, a remote API, a
model download, a licence check — is refused outright. `OfflineBuildTests` scans the web project
for remote references and fails on any.

## Non-NuGet artifacts — OCR engine, models, native runtimes

Local OCR (docs/ocr-engine-evaluation.md) needs things that are not NuGet packages: the
Tesseract engine installer, `eng.traineddata` and `osd.traineddata`. They enter the enclave
through **the same bundle**, under `artifacts/<kind>/`, with the same discipline:

1. In staging, obtain each file, verify its signature or the publisher's published checksum,
   record its SHA-256, origin, version, licence, classification, model or language id, retrieval
   date, and the review in a copy of `scripts/staging/artifacts.manifest.example.json`
   (`emc-artifact-manifest/1`). Kinds: `ocr-engine`, `ocr-model`, `native-runtime`,
   `pdf-rasterizer`.
2. `Export-DependencyBundle.ps1 -ArtifactManifest <that file>` re-hashes every file, refuses one
   whose hash is not the reviewed hash or whose `reviewStatus` is not `approved`, copies it, and
   records it in `manifest.json` with its `kind` and review fields. The export never downloads
   anything.
3. On import, the verifier checks every artifact's hash, kind, licence and approval, and warns
   when a bundle carries no engine or no model — OCR cannot be installed from such a bundle.
4. The engine is installed from `artifacts/ocr-engine/`; the models are copied to the folder
   named by `Ocr:TessdataPath`. `Emc.OcrWorker` reads the engine's version from the installed
   binary and the models' hashes from disk at start, refuses to start if either is missing, and
   records engine version, model identifiers and preprocessing version on every OCR run. No
   component of EMC fetches a model at run time; there is nowhere for it to fetch one from.

Rendering (PDFium, SkiaSharp) arrives as NuGet packages and is covered by the lock files; it is
listed above only because the manifest's `pdf-rasterizer` and `native-runtime` kinds exist for
the case where a rasterizer or runtime is later obtained outside NuGet.
