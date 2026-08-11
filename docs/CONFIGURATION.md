# Configuration

The tracked configuration in `src/Host.Wpf/appsettings.json` is intentionally public-safe and defaults to `Mock` mode. `Demo` is available, but it must be enabled explicitly through configuration or startup arguments.

## Override Order

Configuration is loaded in this order:

1. `appsettings.json`
2. optional `appsettings.Local.json`
3. `ICC_*` environment variables

`ICC_ENV` is also mapped to `Runtime:Environment`.

## Local Override File

Create `src/Host.Wpf/appsettings.Local.json` for machine-specific settings. The file is ignored by git and copied to the build output automatically when it exists.

Example:

```json
{
  "Intune": {
    "Mode": "Demo",
    "DemoHostName": "DEMO-CLIENT-01",
    "TenantId": "contoso.onmicrosoft.com",
    "ClientId": "11111111-1111-1111-1111-111111111111",
    "RedirectUri": "http://localhost"
  }
}
```

Use the local override file for:

- tenant and application identifiers
- proxy settings
- VPN adapter/provider hints
- any environment-specific plugin options

Do not commit secrets, real tenant identifiers, production hostnames, or internal branding.

## Environment Variables

Nested keys use the standard double-underscore convention.

Examples:

```powershell
$env:ICC_Intune__Mode = "Demo"
```

Or for a normal live run:

```powershell
$env:ICC_Intune__Mode = "Live"
$env:ICC_Intune__TenantId = "contoso.onmicrosoft.com"
$env:ICC_Intune__ClientId = "11111111-1111-1111-1111-111111111111"
```

Environment variables are useful in CI or for short-lived local experiments. For day-to-day development, prefer `appsettings.Local.json` so the config stays readable and repeatable.

## Public-Safe Defaults

The committed defaults intentionally avoid environment assumptions:

- `Intune:Mode` is `Mock`
- demo host/user labels are generic and inactive until `Demo` is selected
- Graph tenant and client identifiers are blank
- proxy settings are blank
- VPN detection hints are blank

This keeps a fresh clone runnable without exposing organization-specific data.

## Screenshot Export

The public README screenshots are captured from the running WPF application, not generated separately.

Use:

```powershell
pwsh ./scripts/export-public-screenshots.ps1 -Configuration Debug
```

The launcher starts `Host.Wpf` in screenshot mode, opts into `Demo` mode intentionally, navigates to the documented views, and writes deterministic PNG files to `docs/images`.

You can also enable demo mode for a normal interactive run:

```powershell
dotnet run --project src/Host.Wpf/Host.Wpf.csproj -c Debug -- --intune-mode Demo
```

Mode summary:

- `Mock`: public-safe default, lightweight cloud mock behavior with the usual disconnected local-host UX.
- `Demo`: explicit opt-in, auto-connects the configured demo host, fills the core plugins with deterministic demo data, and simulates actions safely.
- `Live`: real tenant-backed Graph and local diagnostics behavior.
