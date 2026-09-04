# OCR engine evaluation — local, offline, for DA Form 4137 companion scans

**[DESIGN] / [CONTROL].** Nothing in this document is an AR 195-5 requirement. The regulation
says nothing about OCR. What it does say — that the custodian keeps and files the original
DA Form 4137 (2-4d, 2-4f) and the bound ledger accounts for it (2-5a), that the custodian
reviews the submitted form and has the
agent correct and initial errors (2-3g) — is why every extracted value is a *proposal* that a
person verifies, and why no engine choice below changes what is authoritative.

The constraint that shapes this evaluation is the program's, not the regulation's: EMC runs in an
**air-gapped enclave** (docs/air-gapped-build-and-maintenance.md). An OCR engine is acceptable
only if it can be obtained in connected staging, hashed, reviewed, carried across on approved
media, installed from that bundle, and run with **no network access of any kind** — and if the
model files it needs can be treated the same way.

## Criteria

| # | Criterion | Why it matters here |
|---|---|---|
| C1 | Runs fully offline; no model download, licence check, telemetry or cloud call at any point | Disqualifying if not met |
| C2 | Engine and model files are discrete, versioned, hashable artifacts | The bundle manifest records name, version, SHA-256, origin, licence, review status per artifact |
| C3 | Licence permits Government use and redistribution inside the enclave without per-seat activation | No Internet licence validation is possible |
| C4 | Runs on Windows Server (deployment) and Linux (the automated test lane) | One code path, tested where the tests run |
| C5 | Can be isolated in a separate process from IIS | A parser of hostile input must not run in the web worker |
| C6 | Produces per-word (or per-line) confidence and bounding boxes | Confidence bands (OCR-002) and "show the verifier where it read this" need both |
| C7 | Handles printed and typed text on a fixed-layout form well | The DA Form 4137 is a predictable printed layout (OCR-007) |
| C8 | Handles rotation, vertical flip, small skew, noise and varying DPI, or exposes what the caller needs to handle them | Scanner output is not tidy |
| C9 | Handwriting | Names, initials and some entries are handwritten. Honest ceiling: no local engine reads free handwriting reliably; this is a limitation to state, not a criterion to win |
| C10 | Native footprint and supply-chain review burden | Every native library is something the enclave imports, audits and maintains for the life of the system |
| C11 | Maturity and maintenance | A security fix must be obtainable in staging for years |
| C12 | No shell, no scripts, no plug-in loading from untrusted paths | Phase 10 controls |

## Candidates

### Tesseract 5 (Apache-2.0)

Mature open-source engine (LSTM recognizer since 4.0), C++ with a stable command-line interface
and a C API. Models are single `.traineddata` files per language (`eng.traineddata`; `osd` for
orientation and script detection). Windows builds are published as signed installers by the
University of Mannheim library (UB-Mannheim) and the engine is in every Linux distribution.

- C1 ✔. C2 ✔ — installer, `eng.traineddata`, `osd.traineddata` are three hashable files. C3 ✔
  Apache-2.0 engine and models. C4 ✔. C5 ✔ — trivially, as a child process. C6 ✔ — `tsv` output
  gives per-word confidence and box. C7 ✔ good on printed text. C8 partial — `osd` detects
  0/90/180/270° orientation; small-angle deskew is the caller's job. C9 ✗ poor on handwriting.
  C10 low — the engine plus Leptonica and image codecs, all from one installer. C11 ✔ decades of
  maintenance, active. C12 ✔ when invoked with an argument array, no shell.
- .NET integration options: (a) the `Tesseract` NuGet wrapper (5.2.0; P/Invoke into bundled
  native DLLs); (b) the engine as an **external process** with files exchanged through a private
  working directory. (b) is chosen: it gives C5 for free, decouples engine version from the .NET
  build, and makes the engine an `ocr-engine` bundle artifact in its own right rather than a
  native DLL hidden inside a package.

### Windows.Media.Ocr (Windows platform API)

Built into Windows 10/11 and Windows Server 2019+; language packs are OS optional features.

- C1 ✔ at run time. C2 ✗ — the engine and its models are OS components updated by Windows
  Update, not discrete versioned artifacts; the enclave cannot pin or hash them. C4 ✗ — Windows
  only, so the automated test lane could never exercise it. C5 ✔ possible. C6 partial — words with
  boxes, **no confidence**, which defeats OCR-002. C8 weak. C9 slightly better than Tesseract on
  neat handwriting, still unreliable. C10 nil. C11 ✔. Requires a Windows-specific target framework.
- **Rejected**: no pinnable artifact, no confidence, untestable on the lane.

### RapidOCR / PaddleOCR models on ONNX Runtime (Apache-2.0 models; MIT runtime)

Two-stage detection + recognition networks exported to ONNX, run through Microsoft.ML.OnnxRuntime.

- C1 ✔. C2 ✔ — ONNX model files and the runtime package are hashable. C3 ✔. C4 ✔. C5 ✔.
  C6 ✔ per-line confidence and quadrilateral boxes. C7 ✔. C8 ✔ strong on rotated and noisy input.
  C9 ✗ (printed-text models). C10 **high** — a large native inference runtime plus models trained
  and published by a third party whose training data and build pipeline the enclave cannot
  audit; the security review burden is materially larger than Tesseract's. C11 ✔ active but
  fast-moving; model files are re-published frequently. C12 ✔.
- **Deferred, not rejected**: the best technical candidate for difficult scans. If Tesseract's
  accuracy on real (non-public) DA Form 4137 scans proves insufficient, this is the next engine to
  bring through review, behind the same `IOcrEngine` interface. Nothing else in EMC changes.

### EasyOCR, docTR, Kraken, TrOCR (Python / PyTorch)

Require a Python runtime and multi-hundred-megabyte framework wheels. C1 achievable, C10 ✗
(the largest footprint of any option), C4 awkward, C12 harder (interpreter + package loading).
**Rejected** for this system; the handwriting-capable ones (TrOCR) are noted as the only route
to better handwriting later, and would still need a person to verify every field.

### Cloud OCR (Azure AI Vision / Document Intelligence, Google Vision, AWS Textract) and their "disconnected containers"

C1 ✗. The container variants still require Internet connectivity for licence activation and
billing metering. **Disqualified.**

## Decision

**Tesseract 5, run by `Emc.OcrWorker` as an external process** (argument array, no shell, private
working directory, hard timeout, process tree killed on timeout), with:

| Artifact | Kind | Notes |
|---|---|---|
| Tesseract engine installer (Windows) / package (Linux lane) | `ocr-engine` | Pinned version; installer hash recorded in staging against the publisher's checksum |
| `eng.traineddata` | `ocr-model` | From the Tesseract project's `tessdata` (or `tessdata_best`) release, pinned by commit/tag; language id `eng` |
| `osd.traineddata` | `ocr-model` | Orientation and script detection; model id `osd` |
| PDFium + SkiaSharp (already in the NuGet bundle) | `pdf-rasterizer` / `native-runtime` | Page rendering; already locked and hashed as NuGet packages |

Preprocessing that the engine does not do itself — vertical flip and 90°/180°/270° rotation via
`osd`, small-angle deskew by projection profile, contrast normalization, DPI normalization to
300 — is EMC code on the rendered raster, versioned as `PreprocessingVersion` on every `OcrRun`.

The engine is behind `IOcrEngine`. `OcrRun` records the engine name, engine version, model
identifiers and preprocessing version, so a later engine change is an auditable fact per run and
never a silent difference in what was read.

## What this does not claim

- **Handwriting.** Tesseract will read printed and typed entries. Handwritten names, initials,
  and free-text descriptions will mostly land in the Low/Unreadable band and require manual
  entry (OCR-002). That is the designed behaviour, not a failure: an unreadable field is entered
  by a person from the paper, and the record shows it was.
- **Signatures.** Nothing here authenticates a signature. A signature block is detected as
  present-or-absent at most; identity is never inferred from it.
- **Authority.** No OCR result, verified or not, changes the accountability record without the
  reconciliation step (REC-001 to REC-004) and a person's explicit decision.

## Status

The evaluation was done in connected staging terms against public documentation and the
engines' published outputs, and with Tesseract 5.3.4 exercised locally on synthetic pages in this
repository's development environment. **No real DA Form 4137 was used.** Accuracy on the
organization's actual scanner output is measured in the enclave, on real forms that never leave
it, before the OCR feature is relied on operationally.
