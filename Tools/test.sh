#!/usr/bin/env bash
#
# Runs the engine free tests for Runtime/Core: a fake transport, a fake clock
# and in memory storage, no Unity involved. The test project lives in
# "Tests~/Core"; the trailing "~" keeps the Unity Editor from importing it.
#
# Usage: Tools/test.sh [extra dotnet test arguments]
# Exit codes: dotnet test's own, or 2 when the .NET SDK is not installed.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found. Install the .NET 8 SDK to run the Core tests."
  exit 2
fi

echo "Using dotnet $(dotnet --version)"
dotnet test "$ROOT/Tests~/Core" --nologo "$@"
