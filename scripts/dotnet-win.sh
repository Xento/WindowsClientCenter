#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
windows_repo_root="$(wslpath -w "$repo_root")"

command="${1:-build}"
target="${2:-src/Host.Wpf/Host.Wpf.csproj}"

shift $(( $# > 0 ? 1 : 0 ))
shift $(( $# > 0 ? 1 : 0 ))

quote_ps() {
  local value="${1//\'/\'\'}"
  printf "'%s'" "$value"
}

args=( "$command" "$target" "-nologo" "-clp:ErrorsOnly" )

for arg in "$@"; do
  args+=( "$arg" )
done

ps_command="Set-Location $(quote_ps "$windows_repo_root"); dotnet"
for arg in "${args[@]}"; do
  ps_command+=" $(quote_ps "$arg")"
done

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ps_command"
