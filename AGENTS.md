# Repo Scope
- Stack: C#/.NET solution (`WindowsClientCenter.sln`) with WPF/XAML, PowerShell scripts, JSON config, Markdown docs.
- Main code: `src/Host.Wpf`, `src/Plugin.Host`, `src/Plugin.Abstractions`, `src/Intune.Services`, `src/Plugins.DeviceActions`, `src/Plugins.DeviceOverview`, `src/Plugins.ReportingEventsLog`
- Tests: `tests/Plugin.Host.Tests`
- Important config/artifacts: `Directory.Build.props`, `src/Host.Wpf/appsettings.json`, `src/**/*.plugin.json`, `.github/workflows/`, `scripts/`, `build/`, `docs/`
- Ignore by default: `.git/`, `.vs/`, `Visual Studio 18/`, `**/bin/`, `**/obj/`, `out/`, `artifacts/`, `coverage/`, `logs/`, `tmp/`, `cache/`, `vendor/`, `node_modules/`, `packages/`, `libs/`, lockfiles unless dependency resolution is the task

# Architecture And Entry Points
- Host app entry: `src/Host.Wpf/App.xaml` -> `App.xaml.cs`
- Main shell flow: `App.OnStartup()` builds config/DI/logging, then resolves `MainWindowViewModel` and `MainWindow`
- Native plugin flow: `src/Host.Wpf/ViewModels/MainWindowViewModel.cs` -> `src/Plugin.Host/PluginRegistry.cs` -> `src/Plugin.Host/PluginLoader.cs`
- Runtime config starts in `src/Host.Wpf/appsettings.json` and `ICC_*` environment variables
- `Host.Wpf` is the composition root and shell
- `Plugin.Host` loads native plugins from `*.plugin.json` via isolated load contexts
- `Plugin.Abstractions` defines plugin contracts/shared models
- `Intune.Services` switches mock/live behavior through `AddIntuneRuntime(...)`

# Discovery Rules
- Prefer `codebase-memory-mcp` for C# discovery. Order: `index_status` -> `search_graph` -> `get_code_snippet` -> `trace_call_path` -> `query_graph` only when needed.
- First MCP step per session/repo: check `index_status`. If missing/stale or pointing at a different root, run `index_repository(repo_path="<absolute-repo-path>")`, then reuse the exact returned project name.
- Do not open full files right after `search_graph`; read the smallest relevant symbol first with `get_code_snippet`.
- Narrow ambiguous results with `qn_pattern`, `file_pattern`, or module path before expanding context.
- Prefer `rg` for exact text lookup and non-code files (`.xaml`, `.json`, `.md`, `.ps1`, `.yml`), plus known-path snippet reads.
- Prefer narrow reads (`rg -n`, `sed -n`) over broad file scans. Do not scan the whole repo by default.
- Never inspect dependencies or generated output unless the task requires it.
- Keep queries and command output small. Prefer one precise lookup over many overlapping searches.
- Parallelize read-only discovery only. Do not parallelize `dotnet build`, `dotnet test`, `dotnet restore`, or other commands that write shared `obj/`/`bin/` state.

# Editing Rules
- Make the smallest change that solves the task.
- Avoid unrelated refactors, global formatting churn, and full-file rewrites unless the change is inherently file-wide.
- Preserve existing naming, nullability, MVVM structure, and plugin-first architecture.
- Do not edit generated outputs, packaged artifacts, or vendored dependencies.
- Prefer single-method, single-viewmodel, single-plugin, or single-config edits.
- When touching WPF, inspect paired `.xaml`/`.xaml.cs` only if behavior crosses that boundary.
- Keep all added or edited human-readable text in English unless a non-English literal is required for the task.

# Validation
- Run the smallest relevant check first.
- Prefer the repo Windows wrapper from WSL when possible:
  - `./scripts/dotnet-win.sh build src/<Project>/<Project>.csproj -c Debug`
  - `./scripts/dotnet-win.sh test tests/<Project>/<Project>.csproj -c Debug ...`
- For `Host.Wpf`, prefer `./scripts/dotnet-win.sh build src/Host.Wpf/Host.Wpf.csproj -c Debug`; it skips native plugin staging for faster checks.
- Prefer targeted builds/tests before solution-wide validation. Use `dotnet build WindowsClientCenter.sln` only for cross-project wiring changes.
- Prefer quiet output first, for example `-clp:ErrorsOnly -nologo`.
- Run validation serially in this repo. If a parallel attempt hits file locks or shared-output conflicts under `obj/`, `bin/`, `.deps.json`, or `.nuget.g.props`, retry serially.
- Treat non-zero exit code as the primary failure signal; only widen logs when needed.
- Use packaging scripts only when staging, native plugins, or release layout changed:
  - `pwsh ./scripts/stage-native-plugins.ps1 -Configuration Release`
  - `pwsh ./build/package-layout.ps1 -Configuration Release`

# Reasoning And Output
- Use low effort for small local edits and direct follow-ups, medium by default, high for architecture work, cross-project wiring, plugin/host interaction, concurrency/state bugs, or risky refactors.
- Treat model choice as dynamic by task, but do not assume the main session model can be switched mid-turn.
- Use the current session model for normal work. If sub-agents are used, prefer `gpt-5.4-mini` for narrow read-only exploration or simple bounded tasks, `gpt-5.4` for complex or high-risk work, and `gpt-5.3-codex` for code-heavy execution when useful.
- When using a sub-agent, state the chosen model and reasoning level in a short commentary update. In the final response, mention non-default model/reasoning choices that materially affected the result.
- Expand context only for ambiguity, multiple matching implementations/plugins, or DI/config wiring.
- Prefer unified diffs or minimal changed snippets. Keep explanations short and decision-focused. Ask for missing context instead of guessing.

# Project Notes
- Test coverage is mainly in `tests/Plugin.Host.Tests`
- Host runtime defaults live in `src/Host.Wpf/appsettings.json`; environment overrides use `ICC_*`
- Packaging logic lives under `scripts/` and `build/`
- `Directory.Build.props` suppresses `CA1416` repo-wide; do not re-triage those warnings unless that suppression policy is changing

# Plugin UX Rules
- The host is a plugin-first shell. Do not hardcode Intune workflow logic into the host.
- Navigation is driven by the main left tree. Plugin sub-functions belong in `INavigationMenuPlugin` entries using `menuPath` and `navigationTarget`, not separate in-plugin side menus.
- Plugin entries may be top-level and must not be forced under `Devices`.
- Navigation expansion is plugin-configurable through `PluginNavigationEntry.IsExpanded`; subnodes should default to expanded unless a plugin opts out.
- View plugins are stateful and must survive navigation changes without cancelling work or resetting in-memory state.
- Forward plugin status updates to the shared host log via `IHostStatusLogSink`.
- Keep the host aligned with the existing ribbon-first shell, left navigation tree, resizable splitters, and icon-glyph navigation model.
