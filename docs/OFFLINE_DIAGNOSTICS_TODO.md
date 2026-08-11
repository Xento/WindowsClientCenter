# Offline/Cloud Diagnostics TODO

Goal: allow diagnostics to run without direct access to an online client, for example from an Intune diagnostics ZIP or a local folder containing exported artifacts.

## Phase 1: Extend the Input Model

- [ ] Introduce `DiagnosticInputMode` (`RemoteHost`, `LocalFolder`, `IntuneCloudZip`).
- [ ] Extend existing services so they accept a `DiagnosticInput` object instead of only `host`.
- [ ] Extend the UI with input mode switching:
  - Hostname mode (current behavior)
  - Folder mode (`Path to extracted diagnostics`)
  - Cloud mode (`Download from Intune`)

## Phase 2: Shared Intune ZIP/Folder Parser Layer

- [ ] Create a new module under `Intune.Services/Diagnostics/OfflineArtifacts`.
- [ ] Create an `IDiagnosticsArtifactReader` abstraction:
  - Find files such as IME logs, exported event logs, and registry dumps
  - Stream contents, including large files
  - Return warnings and errors in a consistent format
- [ ] Add reader implementations:
  - `FolderArtifactReader` for extracted folders
  - `ZipArtifactReader` for direct ZIP access or temporary extraction
- [ ] Make file-name and path mapping robust enough to handle different export layouts.

## Phase 3: Make Existing Diagnostics Work Offline

- [ ] Move IME timeline/flow analysis onto `IDiagnosticsArtifactReader`.
- [ ] Apply IME app-status correlation to offline logs.
- [ ] Support policy result analysis for exported XML/HTML artifacts without remote PowerShell.
- [ ] Support event log analysis from `.evtx` exports, at minimum for the MDM Admin log.
- [ ] Mark results with `dataSource` (`RemoteHost`, `Zip`, `Folder`) so the report shows where the data came from.

## Phase 4: Intune Cloud Retrieval

- [ ] Determine which Graph endpoints can be used for device diagnostics collect/download.
- [ ] Implement an asynchronous cloud retrieval service:
  - Trigger diagnostics collection if needed
  - Poll status
  - Download the ZIP
- [ ] Add download caching under `artifacts/diagnostics-cache/<deviceId>/<timestamp>`.
- [ ] Add a UI flow:
  - Select device
  - Choose `Latest diagnostics from Intune`
  - Show download progress and the latest retrieval status

## Phase 5: Fallback and Operational Logic

- [ ] Build the diagnostics engine so that when a host is unreachable it can automatically offer a folder/ZIP fallback.
- [ ] Surface warnings and limitations clearly, for example `Realtime checks not available in offline mode`.
- [ ] Normalize timestamps so reports clearly distinguish between `capture time` and `analysis time`.

## Phase 6: Security and Privacy

- [ ] Add a PII redaction option for exports to mask UPNs, device IDs, and tenant IDs.
- [ ] Securely delete temporary extraction folders after analysis.
- [ ] Define maximum ZIP sizes and processing timeouts to avoid UI hangs.

## Phase 7: Tests

- [ ] Add test packages under `tests/TestData/DiagnosticsZip/` (small, medium, malformed).
- [ ] Add parser unit tests for ZIP/FOLDER readers, including damaged archives.
- [ ] Add integration tests for `same analysis, different source` (host vs ZIP/FOLDER) with predictable result parity.
- [ ] Add UI ViewModel tests for mode switching and validation (path exists, device selected, etc.).

## Phase 8: UX and Reporting

- [ ] Show `Input Source`, `Capture Date`, and `Analyzed At` in the report header.
- [ ] Show data coverage per category (IME, Policy, Event Logs, Delivery Optimization).
- [ ] Add a quick action such as `Start analysis from folder` in the Local/Diagnostics area.

## Open Questions

- [ ] Should ZIP files be fully extracted by default, or should stream-based processing be preferred?
- [ ] What are the minimum Intune roles/permissions required for cloud download?
- [ ] How long should downloaded cloud diagnostics packages be cached locally?
- [ ] How should multiple user sessions inside offline artifacts be handled for user-policy correlation?
