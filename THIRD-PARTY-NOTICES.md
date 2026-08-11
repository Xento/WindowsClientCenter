# Third-Party Notices

Windows Client Center includes or is distributed with third-party material.
Those components remain under their own licenses and are expressly excluded
from the Commons Clause condition in the repository root `LICENSE`.

The full license texts referenced below are in `LICENSES/`. Binary release
archives must retain this file, the root `LICENSE`, the complete `LICENSES/`
directory, and `SOURCE-CODE.md`.

## SCCM Client Center Automation Library

- Component: `sccmclictr.automation`
- Copyright: Copyright (c) 2018-2023 Roger Zander
- Upstream: https://github.com/rzander/sccmclictrlib
- Upstream revision: `1c875c00ab04144741247873cea1b69cb25ef1ea`
- License: GNU Lesser General Public License v3.0 or later
- Local scope: the complete `src/SccmCliCtr.Automation/` directory and the
  compiled `sccmclictr.automation.dll`

The local copy is a modified version. The exact changes are recorded in
`src/SccmCliCtr.Automation/MODIFICATIONS.md`. The corresponding source is part
of this repository and is also packaged separately with binary releases. The
DLL is shipped as a separate managed assembly so recipients can replace it
with an interface-compatible modified build.

The LGPL and the GNU GPL on which it supplements are included as
`LICENSES/LGPL-3.0-or-later.txt` and `LICENSES/GPL-3.0.txt`.

## SCCM Client Center UI reference

- Component: SCCM Client Center
- Copyright: Roger Zander and contributors
- Upstream: https://github.com/rzander/sccmclictr
- Upstream revision: `aff9974590d39173e56201f94cd27b080f6552eb`
- License: Microsoft Public License (Ms-PL)
- Local scope: `src/Plugins.MecmAgent/UI/MecmAgentView.xaml`

The local view was independently rewritten for this application, but its
application/update presentation and action layout were adapted using the
upstream XAML files documented in `docs/attribution/mecm-selective-port.md`.
For an unambiguous redistribution boundary, the complete local XAML file is
distributed under the Ms-PL. See `LICENSES/MS-PL.txt`.

## WinGet return-code data

- Component: Windows Package Manager Client return-code documentation
- Copyright: Microsoft Corporation and contributors
- Upstream: https://github.com/microsoft/winget-cli
- Source file: `doc/windows/package-manager/winget/returnCodes.md`
- License: MIT
- Local scope: rows with source `AppInstall` and area `Winget` in
  `src/Shared.Diagnostics/error_codes_library.csv`, and their generated entries
  in `src/Shared.Diagnostics/ErrorCatalog.Community.g.cs`

The numeric codes are factual identifiers. Names and descriptions based on the
upstream documentation are nevertheless attributed here and distributed under
its MIT terms. See `LICENSES/MIT.txt` and
`docs/attribution/error-catalog.md`.

## NuGet and runtime dependencies

The complete restored production-package inventory is maintained in
`docs/third-party-nuget-packages.md` and regenerated from `project.assets.json`
by `scripts/generate-third-party-package-notices.ps1`.

The relevant license families are:

- MIT: .NET libraries, Microsoft.Extensions, Microsoft.Identity.Client,
  PowerShell, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, Newtonsoft.Json,
  Humanizer, and the JsonEverything libraries
- Apache-2.0: Serilog and SQLitePCLRaw packages
- BSD-2-Clause: Markdig.Signed
- Microsoft Software License Terms: the Windows runtime assets in
  Microsoft.Management.Infrastructure.Runtime.Win 3.0.0; these assets are
  included only as a dependency of and solely for use with the embedded
  Microsoft PowerShell SDK, not as a standalone product
- SQLite blessing/public-domain dedication: the native SQLite library carried
  by SQLitePCLRaw

Exact package versions, authors, project URLs, and package license metadata are
listed in the generated inventory. The release also carries version-specific
upstream notices for .NET 8.0.29, WPF 8.0.29, PowerShell 7.4.6, and
CommunityToolkit.Mvvm 8.4.0.

The exact Microsoft.Management.Infrastructure runtime terms are included as
`LICENSES/Microsoft.Management.Infrastructure.Runtime.Win-3.0.0-LICENSE.txt`.
They must remain with any Windows release that contains those runtime files.

## Runtime-downloaded script

`src/Intune.Services/Scripts/Invoke-AutopilotDiagnosticsCommunity.ps1` can, only
after an explicit user action, download `Get-AutopilotDiagnosticsCommunity`
from PowerShell Gallery and accept that package's license. The third-party
script is not stored in this repository and is not included in Window Client
Center release archives. Its own package license applies when a user chooses
to install it.
