# Configuration Reference

Windows Client Center uses Microsoft.Extensions.Configuration. The tracked
`src/Host.Wpf/appsettings.json` is public-safe and defaults to `Mock` mode. Put
machine- or organization-specific values in `src/Host.Wpf/appsettings.Local.json`;
that file is ignored by Git and copied to the build output when it exists.

This document describes the current supported settings. Legacy compatibility
aliases are intentionally omitted.

## Configuration Sources and Precedence

Values loaded later override values loaded earlier:

1. `appsettings.json`
2. optional `appsettings.Local.json`
3. environment variables beginning with `ICC_`
4. final host overrides:
   - `ICC_ENV`, or `dev` when it is unset, becomes `Runtime:Environment`
   - `--intune-mode` overrides `Intune:Mode`
   - screenshot capture mode overrides `Intune:Mode` while screenshots are being exported

Because `Runtime:Environment` is finalized in step 4, changing that key in a
JSON file or through `ICC_Runtime__Environment` currently has no effect. Use
`ICC_ENV` instead.

Nested environment-variable keys use two underscores. For example:

```powershell
$env:ICC_Intune__Mode = "Live"
$env:ICC_Intune__TenantId = "example.onmicrosoft.com"
$env:ICC_Intune__ClientId = "11111111-1111-1111-1111-111111111111"
$env:ICC_Diagnostics__VerboseOperations = "true"
$env:ICC_ENV = "production"
```

`Plugins:NativeDirectory` and the PowerShell plugin's `scriptDirectory` resolve
relative paths from the application base directory. JSON numbers should use a
period as their decimal separator. Invalid or non-positive threshold values
fall back to the defaults.

## Recommended Local Override

Create `src/Host.Wpf/appsettings.Local.json` for values that must not be
published:

```json
{
  "Intune": {
    "Mode": "Live",
    "TenantId": "example.onmicrosoft.com",
    "ClientId": "11111111-1111-1111-1111-111111111111",
    "RedirectUri": "http://localhost",
    "Proxy": "http://proxy.example:8080"
  }
}
```

Do not commit real tenant identifiers, production hostnames, proxy addresses,
internal paths, or organization-specific branding.

## Host Settings

### Runtime

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `Runtime:Environment` | string | `dev` | Displayed in the shell and passed to plugins as the host environment. The current loader finalizes this value from `ICC_ENV`; see the precedence note above. |

### Diagnostics

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `Diagnostics:VerboseOperations` | boolean | `false` | Enables detailed plugin-load summaries and additional timing/operation messages in plugins that support verbose diagnostics. |

Supported plugins can override the global value with
`Plugins:Settings:<plugin-id>:verboseOperations`. The current plugin IDs that
read this override are `device-overview`, `intune-agent`,
`windows-defender-agent`, and `bitlocker-agent`.

### Defender Thresholds

These thresholds are used by Defender diagnostics outside the Device Overview
plugin. Device Overview has separate client-health thresholds documented below.

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `Defender:SecurityIntelligenceWarningThresholdHours` | positive number | `36` | Signature age at which Defender security intelligence becomes a warning. |
| `Defender:SecurityIntelligenceCriticalThresholdHours` | positive number | `72` | Signature age at which Defender security intelligence becomes critical. |

## Intune and Local Runtime

| Key | Type | Default | Allowed values / effect |
| --- | --- | --- | --- |
| `Intune:Mode` | string | `Mock` | `Mock`, `Demo`, or `Live`. Other values are unsupported. |
| `Intune:MecmBackend` | string | `ClientCenterLib` | `ClientCenterLib` uses the bundled automation backend; `PowerShell` uses the PowerShell implementation. Ignored in `Demo` mode. |
| `Intune:DemoHostName` | string | `DEMO-CLIENT-01` | Target hostname used by deterministic demo services. |
| `Intune:DemoTenantId` | string | `demo.example` | Tenant label displayed by demo authentication and demo cloud data. |
| `Intune:DemoUserPrincipalName` | string | `alex.wilson@demo.example` | User principal name used in demo cloud and device-control data. |
| `Intune:DemoConnectedUsersText` | comma-separated string | `DEMO\alex.wilson, DEMO\helpdesk.ops` | Connected users returned by demo services. Empty entries are removed. |
| `Intune:TenantId` | string | empty | Microsoft Entra tenant ID or verified tenant domain used as the MSAL authority in `Live` mode. |
| `Intune:ClientId` | string | empty | Public-client application ID used for Microsoft Graph authentication. With no usable client ID, cloud queries are disabled in `Live` mode. |
| `Intune:RedirectUri` | URI string | `http://localhost` | Redirect URI registered for the public-client application. An empty value lets MSAL select its default redirect URI. |
| `Intune:Proxy` | URI or `host:port` string | empty | Proxy used by the Microsoft Graph HTTP client. `http://` is added when no URI scheme is supplied. Invalid values are ignored. |
| `Intune:PowerShellSessionPoolSize` | integer | `5` | Maximum cached PowerShell sessions per host. Values below `1` are treated as `1`. |
| `Intune:VpnAdapterDescriptionMatch` | string | empty | Adapter-description substring used to identify a VPN adapter in local network diagnostics. |
| `Intune:VpnProviderName` | string | empty | Friendly VPN provider label shown when the configured VPN adapter is detected. |

Mode behavior:

- `Mock` uses real local/remote diagnostics but mock cloud authentication and
  cloud data.
- `Demo` replaces local, MECM, Defender, Intune, Windows Update, and cloud
  services with deterministic demo implementations and simulates actions.
- `Live` uses real local/remote diagnostics and enables Microsoft Graph when a
  usable public-client configuration is present.

## Explorer Targets

`Explorer:Targets` defines the ribbon Explorer menu. Each item can be a leaf,
a submenu folder, or a visual group heading.

| Property | Type | Default | Effect |
| --- | --- | --- | --- |
| `Name` | string | empty | Display name. A leaf falls back to its `Path` when the name is empty. |
| `Type` | string | empty | Use `Group` for a non-clickable group heading. An item with children and any other value, including `Folder`, becomes a submenu folder. |
| `Path` | string | empty | Explorer path for a leaf item. Items without a path and without children are ignored. |
| `MenuPath` | string | empty | Optional slash- or backslash-separated parent menu path. Repeated separators and surrounding spaces are normalized. |
| `Children` | array | empty | Nested target definitions. A target with children acts as a folder or group. |
| `IsDefault` | boolean | `false` | Makes the leaf the default Explorer action. If none is marked, the first leaf is selected; when several are marked, the first one wins. |

`%HOSTNAME%` in `Path` is replaced case-insensitively with the current target
host. A host-dependent entry remains disabled until a host is available.
Absolute local paths and UNC paths without `%HOSTNAME%` are usable without a
connected target.

Example:

```json
{
  "Explorer": {
    "Targets": [
      {
        "Name": "Remote",
        "Type": "Group",
        "Children": [
          {
            "Name": "Remote C$",
            "Path": "\\\\%HOSTNAME%\\c$",
            "IsDefault": true
          }
        ]
      },
      {
        "Name": "Support",
        "Type": "Folder",
        "MenuPath": "Tools",
        "Children": [
          {
            "Name": "Local ProgramData",
            "Path": "C:\\ProgramData"
          }
        ]
      }
    ]
  }
}
```

## Plugin Host

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `Plugins:NativeDirectory` | path string | `plugins/native` | Directory scanned for native `*.plugin.json` manifests. Relative paths are resolved from the application base directory. |
| `Plugins:Settings:<plugin-id>` | object | empty | Plugin-specific settings. Nested values are flattened and passed only to the matching plugin. |

## Device Overview Plugin

The following settings are under
`Plugins:Settings:device-overview`. Every `enabled` setting controls an entire
card or section. The `show...` settings control individual fields within an
enabled section.

### Cloud Device

Prefix: `Plugins:Settings:device-overview:cloudDevice`

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `enabled` | boolean | `true` | Shows the Cloud Device card. |
| `showDevice` | boolean | `true` | Shows the cloud device name. |
| `showPlatform` | boolean | `true` | Shows the cloud operating-system platform. |
| `showCompliance` | boolean | `true` | Shows the reported compliance state. |
| `showCloudLastSync` | boolean | `true` | Shows the latest cloud sync timestamp. |
| `showMdmLastSync` | boolean | `true` | Shows the local MDM sync timestamp. |
| `showImeLastSync` | boolean | `true` | Shows the Intune Management Extension sync timestamp. |
| `showIntuneStatus` | boolean | `true` | Shows the summarized Intune status. |

### Local System

Prefix: `Plugins:Settings:device-overview:localSystem`

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `enabled` | boolean | `true` | Shows the Local System card. |
| `showManufacturer` | boolean | `true` | Shows the hardware manufacturer. |
| `showModel` | boolean | `true` | Shows the model. |
| `showSerialNumber` | boolean | `true` | Shows the serial number. |
| `showWindowsVersion` | boolean | `true` | Shows the Windows edition/version. |
| `showWindowsBuild` | boolean | `true` | Shows the Windows build. |
| `showUpdateRing` | boolean | `true` | Shows the detected update ring. |
| `showPatchStatus` | boolean | `true` | Shows the summarized patch state. |
| `showFreeDiskSpace` | boolean | `true` | Shows free system-drive space. |
| `freeDiskSpaceWarningGb` | positive number | `20` | Warning threshold for free space in GiB. |
| `freeDiskSpaceCriticalGb` | positive number | `10` | Critical threshold for free space in GiB. |

If the critical free-space threshold is greater than the warning threshold, the
two values are swapped.

### Platform Security

Prefix: `Plugins:Settings:device-overview:platformSecurity`

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `enabled` | boolean | `true` | Shows the Platform Security card. |
| `showBitLocker` | boolean | `true` | Shows BitLocker protection state. |
| `showBitLockerDetail` | boolean | `true` | Shows BitLocker volume/method details. |
| `showTpm` | boolean | `true` | Shows TPM readiness. |
| `showTpmVersion` | boolean | `true` | Shows the TPM specification version. |
| `showTpmDetail` | boolean | `true` | Shows detailed TPM state. |
| `showSecureBoot` | boolean | `true` | Shows Secure Boot state. |
| `showCredentialGuard` | boolean | `true` | Shows Credential Guard state. |
| `showVbs` | boolean | `true` | Shows virtualization-based security state. |
| `showMemoryIntegrity` | boolean | `true` | Shows memory-integrity state. |

### System Runtime

Prefix: `Plugins:Settings:device-overview:systemRuntime`

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `enabled` | boolean | `true` | Shows the System Runtime card. |
| `showUptime` | boolean | `true` | Shows system uptime. |
| `showLastReboot` | boolean | `true` | Shows the last reboot timestamp. |
| `showInstallDate` | boolean | `true` | Shows the Windows installation date. |
| `showPendingReboot` | boolean | `true` | Shows summarized pending-reboot state. |
| `showPendingRebootDetail` | boolean | `true` | Shows pending-reboot sources/details. |
| `showWindowsUpdateRestart` | boolean | `true` | Shows Windows Update restart state. |
| `showScheduledRestartTime` | boolean | `true` | Shows a scheduled restart time. |
| `showSessionLock` | boolean | `true` | Shows whether the interactive session is locked. |
| `showLockedSince` | boolean | `true` | Shows when the session was locked. |
| `uptimeWarningDays` | positive number | `14` | Uptime at which health becomes a warning. |
| `uptimeCriticalDays` | positive number | `30` | Uptime at which health becomes critical. |

If the critical uptime threshold is lower than the warning threshold, the two
values are swapped.

### Network

Prefix: `Plugins:Settings:device-overview:network`

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `enabled` | boolean | `true` | Shows the Network card. |
| `showConnectionType` | boolean | `true` | Shows LAN/WLAN connection type. |
| `showActiveAdapter` | boolean | `true` | Shows the active adapter. |
| `showWifiSsid` | boolean | `true` | Shows the connected Wi-Fi SSID. |
| `showVpn` | boolean | `true` | Shows VPN detection state. |
| `showVpnProvider` | boolean | `true` | Shows the configured/detected VPN provider. |
| `showPortAuthenticationSummary` | boolean | `true` | Shows the summarized wired 802.1X/port-authentication state. This supported key is not present in the tracked JSON and can be added when needed. |

### Client Health

Prefix: `Plugins:Settings:device-overview:clientHealth`

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `enabled` | boolean | `true` | Shows the Client Health card. |
| `showOverallHealth` | boolean | `true` | Shows the overall color/state. |
| `showSummary` | boolean | `true` | Shows the combined health explanation. |

Health checks use the prefix
`Plugins:Settings:device-overview:clientHealth:checks`:

| Check / key | Type | Default | Effect |
| --- | --- | --- | --- |
| `defender:enabled` | boolean | `true` | Includes Defender in overall health. |
| `defender:showStatus` | boolean | `true` | Shows Defender status. |
| `defender:showDetail` | boolean | `true` | Shows the Defender health explanation. |
| `defender:showDefinitionAge` | boolean | `true` | Shows security-intelligence age. |
| `defender:signatureWarningHours` | positive number | `36` | Warning threshold for signature age. |
| `defender:signatureCriticalHours` | positive number | `72` | Critical threshold for signature age. |
| `defender:scanWarningDays` | positive number | `14` | Warns when the last scan is older than this value. |
| `entraJoin:enabled` | boolean | `true` | Includes Entra join state in health. |
| `entraJoin:showStatus` | boolean | `true` | Shows Entra join state. |
| `adJoin:enabled` | boolean | `true` | Includes Active Directory join state in health. |
| `adJoin:showStatus` | boolean | `true` | Shows Active Directory join state. |
| `intuneEnrollment:enabled` | boolean | `true` | Includes Intune enrollment in health. |
| `intuneEnrollment:showStatus` | boolean | `true` | Shows Intune enrollment state. |
| `enrollmentUrls:enabled` | boolean | `true` | Checks enrollment URL configuration. |
| `freeDiskSpace:enabled` | boolean | `true` | Includes free disk space in health. |
| `uptime:enabled` | boolean | `true` | Includes uptime in health. |

If the Defender critical signature threshold is lower than the warning
threshold, the two values are swapped.

### Delivery Optimization

Prefix: `Plugins:Settings:device-overview:deliveryOptimization`

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `enabled` | boolean | `true` | Enables the Delivery Optimization view. |
| `showSummary` | boolean | `true` | Shows its summary. |
| `showActiveJobs` | boolean | `true` | Shows active jobs. |
| `showCurrentMetrics` | boolean | `true` | Shows current performance counters. |
| `showMonthlyMetrics` | boolean | `true` | Shows month-to-date counters. |
| `showPeerSnapshot` | boolean | `true` | Shows peer information. |
| `showConfiguration` | boolean | `true` | Shows Delivery Optimization configuration. |
| `showSourceDistribution` | boolean | `true` | Shows source totals/distribution. |
| `showTransferTimeline` | boolean | `true` | Shows the transfer timeline. |
| `showNotes` | boolean | `true` | Shows interpretation notes. |

### Port Authentication

Prefix: `Plugins:Settings:device-overview:portAuthentication`

This supported section is not present in the tracked JSON. Add it to an override
file to customize the view.

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `enabled` | boolean | `true` | Enables the Port Authentication view. |
| `showSummary` | boolean | `true` | Shows the overall 802.1X summary. |
| `showChecks` | boolean | `true` | Shows individual health checks. |
| `showProfiles` | boolean | `true` | Shows wired authentication profiles. |
| `showCertificates` | boolean | `true` | Shows matching authentication certificates. |
| `showEvents` | boolean | `true` | Shows relevant authentication events. |
| `showRemediation` | boolean | `true` | Shows remediation controls. |

Example:

```json
{
  "Plugins": {
    "Settings": {
      "device-overview": {
        "network": {
          "showPortAuthenticationSummary": true
        },
        "portAuthentication": {
          "enabled": true,
          "showEvents": false,
          "showRemediation": true
        }
      }
    }
  }
}
```

## Device Services Plugin

Prefix: `Plugins:Settings:device-services-view`

`filters` is an ordered array. If no valid filters are configured, the plugin
uses its built-in `All services` and `MECM / Intune related` categories. If no
configured category includes all services, an `All services` category is
inserted automatically.

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `filters[].displayName` | string | none | Required label for the category. Entries with an empty label are skipped. |
| `filters[].includeAllServices` | boolean | `false` | Includes every returned service and ignores `serviceNames` for matching. |
| `filters[].serviceNames` | delimited string | empty | Case-insensitive service names separated by commas, semicolons, or line breaks. Duplicate names are removed. |

The first filter becomes the initially selected category.

## Device Processes Plugin

Prefix: `Plugins:Settings:device-processes-view`

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `defaultViewMode` | string | `list` | `list` or `tree`; any other value selects `list`. |
| `refreshIntervals` | delimited string | `0,5,10,30,60` | Non-negative seconds separated by commas, semicolons, or line breaks. Invalid and duplicate values are removed, values are sorted, and `0` (off) is always added. |
| `defaultRefreshIntervalSeconds` | integer | `0` | Initial refresh interval. It falls back to `0` unless the same value exists in `refreshIntervals`. |

## PowerShell Scripts Plugin

Prefix: `Plugins:Settings:powershell-scripts`

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `scriptDirectory` | path string | `PSScripts` | Directory scanned for script metadata and scripts. Relative paths are resolved from the application base directory. If it does not exist, the plugin tries the staged native-plugin `PSScripts` directory and then its built-in fallbacks. |

## Environment-Only Override

`ICC_POLICY_DEFINITIONS_ROOT` is read directly by the local Intune policy-result
implementation and is not a JSON setting. It overrides the Windows policy
definitions root used while resolving ADMX metadata. Use it only for diagnostics
or controlled test environments.

## Public-Safe Defaults

The committed defaults intentionally avoid environment assumptions:

- `Intune:Mode` is `Mock`.
- Demo host and user labels are generic and inactive until `Demo` is selected.
- Graph tenant and client identifiers are empty.
- Proxy and VPN detection hints are empty.
- Explorer targets use only local standard paths or `%HOSTNAME%` templates.

## Screenshot Export

The public README screenshots are captured from the running WPF application in
explicit `Demo` mode:

```powershell
pwsh ./scripts/export-public-screenshots.ps1 -Configuration Debug
```

For a normal interactive demo run:

```powershell
dotnet run --project src/Host.Wpf/Host.Wpf.csproj -c Debug -- --intune-mode Demo
```
