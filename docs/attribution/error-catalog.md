# Error Catalog Attribution

The editable catalog is `src/Shared.Diagnostics/error_codes_library.csv`.
`src/Shared.Diagnostics/ErrorCatalog.Community.g.cs` is generated from that
catalog and must not be treated as an independent source.

Rows whose source is `AppInstall` and whose area is `Winget` were transcribed
and normalized from the official Windows Package Manager return-code list:

- Repository: https://github.com/microsoft/winget-cli
- Source: `doc/windows/package-manager/winget/returnCodes.md`
- License: MIT
- Copyright: Microsoft Corporation and contributors

The remaining numeric Windows, Win32, HRESULT, MSI, and application error codes
are factual identifiers with short operational summaries assembled from public
platform documentation. No other bulk third-party catalog was identified in
the repository audit. New imports from third-party catalogs must add a source,
pinned version or commit, license, and exact row scope to this document before
distribution.
