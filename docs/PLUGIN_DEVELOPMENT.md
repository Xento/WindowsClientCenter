# Plugin Development

## Native plugin contracts

From `Plugin.Abstractions`:

- `IViewPlugin`
- `IActionPlugin`
- `IBackgroundTaskPlugin`
- `IPluginManifest`
- `IPluginContext`

## Manifest format

Each native plugin must ship a `*.plugin.json` file in the same directory as the DLL.

Example:

```json
{
  "id": "device-overview",
  "displayName": "Device Overview",
  "version": "1.0.0",
  "capability": "View",
  "menuPath": "Devices/Overview",
  "minHostVersion": "1.0.0",
  "assembly": "Plugins.DeviceOverview.dll",
  "type": "WindowsClientCenter.Plugins.DeviceOverview.DeviceOverviewPlugin"
}
```

## Discovery

Native host loads all `*.plugin.json` files from `Plugins:NativeDirectory`.
