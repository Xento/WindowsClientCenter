# Modifications to SCCM Client Center Automation Library

This directory is based on `sccmclictrlib` by Roger Zander at commit
`1c875c00ab04144741247873cea1b69cb25ef1ea`, licensed under LGPL v3 or later.

The local source was compared against that exact upstream archive on
2026-08-11. Apart from line-ending differences, the following modifications
were identified:

- 2026: ported the project from the legacy .NET Framework project format to an
  SDK-style `net8.0` project and replaced framework references with current
  NuGet package references. Known warnings produced by unchanged legacy source
  remain visible but are excluded from repository-wide warnings-as-errors
  enforcement for this project only.
- 2026: replaced the generated `.resx` resource accessor with
  `Properties/Resources.cs`; retained the upstream resource content and moved
  the health-check script to an embedded text resource.
- 2026-08-11: replaced an upstream internal laboratory hostname in a
  documentation/example URL in `Policy.cs` with the reserved
  `example.invalid` domain. This does not change runtime behavior.
- 2026: omitted obsolete Visual Studio/TFS metadata, the class diagram, legacy
  app configuration, `.resx` designer inputs, solution metadata, and the
  upstream NuGet packaging file from this application-local port.
- 2026: added the adjacent LGPL license, prominent modification headers, and
  this modification record.

All other included functional `.cs` source files and the embedded
`Resources/HealthCheck.ps.txt` match the pinned upstream revision apart from
line endings.

These modifications and the complete directory remain licensed under LGPL v3
or later. Copyright in the upstream material remains with Roger Zander and the
upstream contributors.
