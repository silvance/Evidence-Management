# The release bundle — one archive, the executables, built from a commit inside the air gap

**[CONTROL] / [DESIGN]** (SEC-013). AR 195-5 says nothing about how software is packaged. This is the
program's control: a deployment installs exactly one reviewed archive whose contents are named,
hashed and traceable to a commit, and nothing else.

## What it is

`scripts/release/New-ReleaseBundle.ps1` (Windows, the release path) and
`scripts/release/new-release-bundle.sh` (the Linux test lane) produce, under the git-ignored
`release/` folder:

```
emc-<version>-<rid>-<commit>/
  web/                     Emc.Web published for the runtime: Emc.Web.exe, Emc.Web.dll,
                           web.config with the aspNetCore handler (IIS in-process), appsettings.json
  worker/                  Emc.OcrWorker published for the runtime: Emc.OcrWorker.exe (the
                           Windows Service and the render child process), appsettings.json
  db/                      schema-v1.sql (applied by the DBA, never by the application) and README
  scripts/deploy/          Test-EmcOcrWorkerPrerequisites, Set-EmcOcrWorkerConfig,
                           Install-EmcOcrWorker, Uninstall-EmcOcrWorker
  scripts/verify/          Verify-ReleaseBundle.ps1, verify-release-bundle.sh
  docs/                    this document, the worker deployment procedure, the air-gap
                           procedure, the dependency advisories, the slice reports
  release-manifest.json    schema emc-release-bundle/1: product, version, informational
                           version (version+commit), commit, branch, dirty flag, build time,
                           SDK, runtime, restore mode and the dependency bundle's manifest
                           hash and audit date, test counts, components with their entry
                           points, the schema script, and every file's SHA-256 and size
  MANIFEST.sha256          the same files as a plain `sha256sum -c` list
emc-<version>-<rid>-<commit>.zip
emc-<version>-<rid>-<commit>.zip.sha256
```

The version is `<VersionPrefix>` in `Directory.Build.props`; the commit is stamped into every
assembly's informational version (`0.9.0+<commit>`), so a binary on a server names the source it
came from.

## How it is produced — the air-gapped path

```
pwsh scripts/release/New-ReleaseBundle.ps1                    # win-x64, offline, from dependency-bundle/
pwsh scripts/release/New-ReleaseBundle.ps1 -TesseractPath 'C:\Program Files\Tesseract-OCR\tesseract.exe' -TessdataPath 'C:\Program Files\Tesseract-OCR\tessdata'
```

1. The working tree must be clean (a release is built from a commit) and the installed SDK
   must be the pinned one.
2. The dependency bundle is verified (`Verify-DependencyBundle.ps1`), then restore runs in
   **locked mode from the bundle alone** (`NuGet.Offline.Config`, `EMC_OFFLINE=true`). Nothing
   is resolved that the lock files do not name; nothing is downloaded.
3. Build (Release) and **test**, with nothing restored from here on. Any failing test stops the
   run; no bundle is produced. The real-engine OCR tests run when the engine and models are
   named; otherwise the script says they were skipped.
4. `Emc.Web` and `Emc.OcrWorker` are **published framework-dependent for the runtime**. Never
   self-contained: the server's ASP.NET Core Hosting Bundle, itself from the dependency bundle's
   `prerequisites/`, supplies the runtime, and the runtime is patched by replacing that one
   installer rather than by rebuilding every application.
5. `appsettings.Development.json` and anything that could hold a secret are removed; a
   published configuration file carrying a credential-like value stops the run.
6. Manifests, archive, hash. The producer runs the verifier over its own output before it
   reports success.

**Runtimes.** `win-x64` is the deployment target (IIS in-process; the worker as a Windows
Service). `linux-x64` exists only so the Linux test lane can produce and exercise real
executables; it is not a deployment target and its manifest says so. Both are pinned in
`Directory.Build.props` (`RuntimeIdentifiers`) so the runtime-specific dependency graph is in
every `packages.lock.json`, and the apphost packs travel in the dependency bundle
(`Export-DependencyBundle.ps1`, step 3b). The runtime packs are not downloaded at all
(`EnableRuntimePackDownload=false`): a framework-dependent publish never uses them, and it
keeps a self-contained publish impossible by construction.

**Staging dry run.** `-Connected` (`--connected`) restores from nuget.org instead of the
dependency bundle, still in locked mode. The archive is named `-staging-dryrun` and the manifest
records it. It exercises the workflow; it is not a release.

## Verifying a bundle before deployment

```
pwsh scripts/verify/Verify-ReleaseBundle.ps1 -Path emc-0.9.0-win-x64-<commit>.zip
scripts/verify/verify-release-bundle.sh emc-0.9.0-linux-x64-<commit>.zip
```

Both check: the archive against its `.sha256`; every file against `MANIFEST.sha256` **and**
`release-manifest.json` (so the two cannot disagree); the entry points, the IIS `web.config`
handler, the schema script and the deploy scripts are present; no development-only or
secret-bearing file is inside; no published configuration carries a credential-like value; the
tests ran and none failed. A dry run, a skipped test run or a dirty working tree is called out
as "not a release". They use no network.

## Deploying from it

1. **Database.** The DBA applies `db/schema-v1.sql` (idempotent) and grants the two identities
   the least privilege in `db/README.md`. The application never migrates on start-up (AUD-012)
   and its login has no DDL rights, so it cannot drop the append-only triggers it depends on.
2. **Web.** Copy `web/` to the IIS site's physical path. The published `web.config` already
   names `Emc.Web.exe`, in-process hosting, and the request limit for a 50 MB upload
   (DOC-004). Set the connection string (Windows authentication, no password) and
   `SourceDocuments:RootPath` (outside the site's physical path) in `appsettings.json`;
   enable Windows Authentication and disable Anonymous on the site (IAM-003).
3. **Worker.** Copy `worker/` to the worker host and follow `docs/ocr-worker-deployment.md`
   with the bundle's `scripts/deploy/`: prerequisites check, configuration written from
   parameters and the reviewed artifact manifest (the approved engine and model hashes),
   service installation under a dedicated identity.
4. **Record** the archive's SHA-256 and the manifest's commit in the deployment record. An
   upgrade is the same procedure with the next bundle; nothing is patched in place.

## What it deliberately does not do

- It does not fetch anything. A missing package in the bundle is a failed restore, by design.
- It does not bundle the .NET runtime into the applications (no self-contained publish, no
  single-file, no trimming): the runtime is a reviewed prerequisite with its own patch path.
- It does not produce an installer, a container image or an MSI. The archive plus the scripts
  it carries are the unit of deployment; the organization's own change process wraps them.
- It does not sign the archive. Authenticity travels by the organization's media-transfer
  process and the recorded SHA-256; add code signing there if the accreditation requires it.
- It does not run the SQL Server release-validation lane unless `EMC_SQLSERVER_TEST_CONNECTION`
  names an approved instance; the manifest records whether that lane ran.
