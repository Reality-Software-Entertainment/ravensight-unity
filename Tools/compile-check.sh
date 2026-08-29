#!/usr/bin/env bash
#
# Compile checks Runtime/Core, the part of the SDK that has no UnityEngine
# dependency. It will not catch mistakes in the MonoBehaviour layer, which
# needs the Unity compiler, but it does prove the protocol logic builds.
#
# Usage: Tools/compile-check.sh
# Exit codes: 0 compiled, 1 compile errors, 2 no C# compiler found.

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CORE="$ROOT/Runtime/Core"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT

echo "Compile checking $CORE"

if command -v dotnet >/dev/null 2>&1; then
  echo "Using dotnet $(dotnet --version)"
  mkdir -p "$OUT/proj"
  cat > "$OUT/proj/CoreCheck.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$(CoreDir)/*.cs" />
  </ItemGroup>
</Project>
CSPROJ
  dotnet build "$OUT/proj/CoreCheck.csproj" -p:CoreDir="$CORE" -v quiet --nologo
  exit $?
fi

for COMPILER in csc mcs; do
  if command -v "$COMPILER" >/dev/null 2>&1; then
    echo "Using $COMPILER"
    "$COMPILER" -target:library -langversion:7.3 -nologo \
      -out:"$OUT/RealitySE.Ravensight.Core.dll" "$CORE"/*.cs
    exit $?
  fi
done

echo "No C# compiler found (looked for dotnet, csc, mcs)."
echo "Install the .NET SDK (brew install --cask dotnet-sdk) or open the package"
echo "in Unity, which compiles it as part of the normal domain reload."
exit 2
