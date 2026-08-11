# MECM Third-Party Attribution and License Boundary

Windows Client Center uses two Roger Zander projects. They have different
licenses and are kept outside the repository's Commons Clause scope.

## SCCM Client Center Automation Library

- Repository: https://github.com/rzander/sccmclictrlib
- Pinned commit: `1c875c00ab04144741247873cea1b69cb25ef1ea`
- License: LGPL v3 or later
- Upstream copyright: Roger Zander and contributors

The complete `src/SccmCliCtr.Automation/` directory is an application-local
`net8.0` port of the upstream `sccmclictr.automation` project. Contrary to an
earlier version of this document, this is a real runtime dependency when the
configured MECM backend is `ClientCenterLib`. It is built from source and
distributed as the separate `sccmclictr.automation.dll` assembly.

The exact porting changes are listed in
`src/SccmCliCtr.Automation/MODIFICATIONS.md`. The application-side
`src/Intune.Services/Runtime/SccmClientCenterMecmService.cs` is original adapter
code under the root project license. It uses the LGPL assembly's public API but
does not incorporate the upstream implementation source. This keeps the
LGPL-covered binary boundary at the separate, replaceable
`sccmclictr.automation.dll`.

Full LGPL/GPL texts and corresponding-source instructions are provided in
`LICENSES/` and `SOURCE-CODE.md`.

## SCCM Client Center UI

- Repository: https://github.com/rzander/sccmclictr
- Pinned commit: `aff9974590d39173e56201f94cd27b080f6552eb`
- License: Microsoft Public License (Ms-PL)
- Upstream copyright: Roger Zander and contributors
- Relevant upstream references:
  - `SCCMCliCtrWPF/SCCMCliCtrWPF/Controls/ApplicationGrid.xaml`
  - `SCCMCliCtrWPF/SCCMCliCtrWPF/Controls/SWUpdatesGrid.xaml`
  - `SCCMCliCtrWPF/SCCMCliCtrWPF/Controls/SWAllUpdatesGrid.xaml`

`src/Plugins.MecmAgent/UI/MecmAgentView.xaml` is a new WPF implementation for
Windows Client Center, but its application/update presentation, action layout,
and several labels were adapted using those upstream views. The complete local
XAML file is therefore distributed under the Ms-PL to avoid an ambiguous
file-level boundary. The full Ms-PL text is in `LICENSES/MS-PL.txt`.
