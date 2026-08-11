# Project Index

## Solution
- `WindowsClientCenter.sln`
- Shared build settings: `Directory.Build.props`
- Primary language: C#
- Secondary formats: XAML, PowerShell, JSON, Markdown
- Main toolchain: `dotnet`, NuGet, MSBuild, PowerShell

## Execution Entry Points
- Host app: `src/Host.Wpf/App.xaml` -> `src/Host.Wpf/App.xaml.cs`
- Main window: `src/Host.Wpf/MainWindow.xaml` + `src/Host.Wpf/MainWindow.xaml.cs`
- Host viewmodel: `src/Host.Wpf/ViewModels/MainWindowViewModel.cs`
- Runtime config: `src/Host.Wpf/appsettings.json`

## Project Map
- `src/Host.Wpf`
  - Type: WPF host (`net8.0-windows`, `WinExe`)
  - Role: composition root, DI/config/logging, shell UI
  - Key files: `App.xaml.cs`, `MainWindow.xaml`, `ViewModels/MainWindowViewModel.cs`, `Runtime/`
- `src/Plugin.Abstractions`
  - Type: shared contracts/models (`net8.0`)
  - Role: plugin interfaces and shared DTOs
  - Key files: `Contracts/`, `Models/`
- `src/Plugin.Host`
  - Type: native plugin loader (`net8.0`)
  - Role: manifest discovery, load context, lifecycle, registry
  - Key files: `PluginLoader.cs`, `PluginRegistry.cs`, `PluginLifecycle.cs`, `Internal/CollectiblePluginLoadContext.cs`
- `src/Intune.Services`
  - Type: service layer (`net8.0`)
  - Role: auth, device query/action, live vs mock runtime wiring
  - Key files: `Runtime/ServiceCollectionExtensions.cs`, `Contracts/`, `Models/`
- `src/Plugins.DeviceActions`
  - Type: native plugin (`net8.0`)
  - Key files: `DeviceActionsPlugin.cs`, `Plugins.DeviceActions.plugin.json`
- `src/Plugins.DeviceOverview`
  - Type: native WPF plugin (`net8.0-windows`)
  - Key files: `DeviceOverviewPlugin.cs`, `UI/DeviceOverviewView.xaml`, `ViewModels/DeviceOverviewViewModel.cs`, `Plugins.DeviceOverview.plugin.json`
- `src/Plugins.ReportingEventsLog`
  - Type: native WPF plugin (`net8.0-windows`)
  - Key files: `ReportingEventsLogPlugin.cs`, `UI/ReportingEventsLogView.xaml`, `ViewModels/ReportingEventsLogViewModel.cs`, `Plugins.ReportingEventsLog.plugin.json`
- `tests/Plugin.Host.Tests`
  - Type: xUnit test project (`net8.0`)
  - Role: plugin host coverage
  - Key files: `PluginLoaderTests.cs`

## Wiring Paths
- Host startup: `App.OnStartup()` -> binds config -> registers services -> resolves `MainWindowViewModel`
- Native plugins: `MainWindowViewModel.LoadPluginsAsync()` -> `PluginRegistry` -> `PluginLoader.DiscoverAsync()`
- Intune services: `AddIntuneRuntime(...)` selects mock or live implementations

## Config And Manifests
- Host config: `src/Host.Wpf/appsettings.json`
- Plugin manifests: `src/**/*.plugin.json`
- CI: `.github/workflows/windows-ci.yml`

## Tests And Build Scripts
- Targeted tests: `dotnet test tests/Plugin.Host.Tests/Plugin.Host.Tests.csproj`
- Packaging scripts:
  - `scripts/stage-native-plugins.ps1`
  - `build/package-layout.ps1`
- Plugin development notes: `docs/PLUGIN_DEVELOPMENT.md`

## Ignore During Navigation
- `.git/`, `.vs/`, `Visual Studio 18/`
- `**/bin/`, `**/obj/`, `out/`, `artifacts/`, `coverage/`, `logs/`
- External dependency trees if they appear: `vendor/`, `node_modules/`, `packages/`, `libs/`
