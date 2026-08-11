# Contributing

## Ground Rules

- Keep pull requests focused and small.
- Preserve the plugin-first architecture. New device workflows belong in plugins or runtime services, not in the host shell.
- Prefer public-safe defaults and mock data in docs, screenshots, and tests.
- Update user-facing docs when behavior or setup changes.

## Development Setup

Requirements:

- Windows 10/11
- .NET 8 SDK
- PowerShell 7
- WSL is optional; the repo includes `./scripts/dotnet-win.sh` for invoking the Windows SDK from WSL

Useful commands:

```powershell
dotnet restore WindowsClientCenter.sln
dotnet build src/Host.Wpf/Host.Wpf.csproj -c Debug
dotnet test tests/Intune.Services.Tests/Intune.Services.Tests.csproj -c Debug
```

From WSL:

```bash
./scripts/dotnet-win.sh build src/Host.Wpf/Host.Wpf.csproj -c Debug -clp:ErrorsOnly -nologo
./scripts/dotnet-win.sh test tests/Intune.Services.Tests/Intune.Services.Tests.csproj -c Debug -clp:ErrorsOnly -nologo
```

## Configuration

- Keep `src/Host.Wpf/appsettings.json` public-safe.
- Put machine-specific values in `src/Host.Wpf/appsettings.Local.json`.
- Never commit real tenant identifiers, hostnames, or internal branding unless the task explicitly requires a sanitized fixture.

## Contribution License

By submitting a contribution, you agree to license your original contribution
under the repository's root [LICENSE](LICENSE), unless a file is clearly marked
as a third-party component governed by different terms. Do not submit code or
other material that you are not authorized to license on those terms.

## Validation

Before opening a pull request:

- build the affected project(s)
- run the smallest relevant test project(s)
- export screenshots from the running application when README gallery assets change:

```bash
pwsh ./scripts/export-public-screenshots.ps1 -Configuration Debug
```

## Plugin Development

The host is a shell, not the place for feature-specific business logic. New feature work should usually land in:

- `src/Plugins.*` for UI and workflow orchestration
- `src/Intune.Services` for reusable diagnostics or action services
- `src/Plugin.Abstractions` only when a new shared contract is truly required

See [docs/PLUGIN_DEVELOPMENT.md](docs/PLUGIN_DEVELOPMENT.md) for plugin structure and navigation guidance.
