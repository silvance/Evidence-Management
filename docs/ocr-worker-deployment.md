# Deploying the OCR and render worker (`Emc.OcrWorker`) — air-gapped, non-interactive

**[CONTROL] / [DESIGN].** The worker is the process that parses hostile input: it renders
companion-copy pages (in a child process it starts per page) and runs the local OCR engine. It
must run as a Windows Service under an identity that can do exactly its job and nothing else,
with no network but SQL Server, and it must refuse to start when anything it depends on is not
what the organization approved. Everything below is done inside the air-gapped environment from
the verified dependency bundle (`docs/air-gapped-build-and-maintenance.md`). Nothing here
requires, or is allowed, an Internet connection.

## What the worker needs

| Item | Where it comes from | Why |
|---|---|---|
| `Emc.OcrWorker` published folder | `dotnet publish src/Emc.OcrWorker -c Release -r win-x64 --self-contained false` on the air-gapped build host, from the offline restore | The service executable; also the render child (`Emc.OcrWorker.exe render ...`) |
| ASP.NET Core Hosting Bundle for the pinned runtime | bundle `prerequisites/` | The shared framework the worker runs on |
| Tesseract 5 engine | bundle `artifacts/ocr-engine/` | Installed to `C:\Program Files\Tesseract-OCR` |
| `eng.traineddata`, `osd.traineddata` | bundle `artifacts/ocr-model/` | Copied to the engine's `tessdata` folder |
| The reviewed artifact manifest (`emc-artifact-manifest/1`) | travels with the bundle | Source of the approved SHA-256 values the worker verifies at start (OCR-017) |
| A dedicated identity | the domain: a group Managed Service Account (recommended) or a service account with no interactive logon | Least privilege; a gMSA has no password to handle |
| A SQL login for that identity | created by the DBA | Windows authentication only; SELECT on `SourceDocuments`, SELECT/INSERT/UPDATE on `DocumentRenderJobs` and `OcrJobs`, SELECT/INSERT on `DocumentRenderRuns`, `DocumentRenderPages`, `OcrRuns`, `OcrRunPages`, `ExtractedFields`; no `db_owner`, no DDL |
| Three folders | created by the deployer | the source-document store (shared with the web application, Modify), a render work folder (Modify), an OCR work folder (Modify) |

## Steps

1. **Publish** on the air-gapped build host and copy the output to the worker host, e.g.
   `C:\Emc\OcrWorker`. Copy nothing else there.
2. **Install the engine** from the bundle's `artifacts/ocr-engine/` and copy the models into
   its `tessdata` folder.
3. **Write the configuration** from parameters and the manifest — no editing by hand of
   anything that carries a hash:

   ```powershell
   pwsh scripts\deploy\Set-EmcOcrWorkerConfig.ps1 -InstallRoot C:\Emc\OcrWorker `
       -SqlServerInstance <server\instance> -Database Emc `
       -SourceDocumentsRoot D:\EmcSourceDocuments -RenderWorkRoot D:\EmcRenderWork -OcrWorkRoot D:\EmcOcrWork `
       -ArtifactManifest <path to the reviewed artifacts.manifest.json>
   ```

   This writes a Windows-authentication connection string (never a password), points
   `SourceDocuments:RenderHelperPath` at the worker's own executable, and copies the approved
   SHA-256 of the installed `tesseract.exe` (from the engine entry's `installedFiles`) and of
   the two model files into `Ocr:ApprovedArtifactHashes`.
4. **Check** before anything is registered:

   ```powershell
   pwsh scripts\deploy\Test-EmcOcrWorkerPrerequisites.ps1 -InstallRoot C:\Emc\OcrWorker -WebRoot <IIS site physical path>
   ```

   Every line must read `PASS`: the render helper is the worker executable; the three folders
   are absolute and outside both the install folder and the web root; the engine and models
   exist and hash to their approved values; the connection string is Windows-authenticated,
   encrypted and password-free; the lease arithmetic the worker enforces at start holds.
5. **Install** the service (administrator):

   ```powershell
   pwsh scripts\deploy\Install-EmcOcrWorker.ps1 -InstallRoot C:\Emc\OcrWorker -ServiceAccount DOMAIN\gMSA-EmcOcr$ -SqlServerHost <sql host>
   ```

   The script re-runs the checks, grants the identity Read & Execute on the install and
   engine folders and Modify on the three data folders (nothing to anyone else), registers
   `EmcOcrWorker` (automatic delayed start) with restart-on-failure recovery, adds outbound
   firewall block rules for the worker executable and `tesseract.exe` (with an allow for the
   SQL Server host only), starts the service and reports its status. A conventional service
   account is prompted for through `Get-Credential`; no script accepts a password parameter and
   none is written anywhere.
6. **Confirm** in the Application event log (source `EmcOcrWorker`): the start-up line names
   the engine version, the model identifiers by hash and `artifacts verified against approved
   hashes: True`. A start-up refusal names its category (`ArtifactNotApproved`, `ModelMissing`,
   `EngineUnavailable`) or the configuration key at fault; it never names a file's content.

## What the service does at start, and refuses

- Refuses without `SourceDocuments:RenderHelperPath` pointing at an existing file (pages are
  never rendered in the service process — DOC-014).
- Refuses with an empty `Ocr:ApprovedArtifactHashes` or any installed artifact whose hash
  differs from its approved value (OCR-017) — before the engine binary is executed at all.
- Refuses when `Ocr:LeaseSeconds` is under twice `Ocr:PageTimeoutSeconds`, or
  `SourceDocuments:RenderLeaseSeconds` under twice `SourceDocuments:RenderTimeoutSeconds`
  (OCR-011: a lease that could expire mid-page would let a running job be executed twice).
- Reconciles the blob store against the database at start and every `Ocr:OrphanSweepHours`
  (OCR-018); counts only are logged.

## Stopping, upgrading, uninstalling

- **Stop** (`Stop-Service EmcOcrWorker`): the loop stops between jobs. A job leased at that
  moment keeps its lease until it expires and is then retried; a render child in flight is
  killed by the parent's timeout. Nothing is half-written: page blobs without a run row are
  removed by the processor or, after a crash, by the sweep.
- **Upgrade**: stop, replace the published folder, re-run `Set-EmcOcrWorkerConfig.ps1` if the
  manifest changed (a new engine or model means new approved hashes), re-run the checks, start.
- **Uninstall** (`scripts\deploy\Uninstall-EmcOcrWorker.ps1`): stops and deletes the service and
  its firewall rules. The source-document store, the work folders and the engine are left in
  place; an uninstall deletes nothing under evidence-room accountability.

## Logging

Identifiers, counts, durations, categories and exception type names. Never a word an engine
read, never an engine message, never a filename, never a connection string. The event-log source
is `EmcOcrWorker`; the same lines go to the console when run by hand.
