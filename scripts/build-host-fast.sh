#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
config="${1:-Debug}"

"$script_dir/dotnet-win.sh" build "src/Host.Wpf/Host.Wpf.csproj" -c "$config"
