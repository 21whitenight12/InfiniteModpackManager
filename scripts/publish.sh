#!/usr/bin/env bash
# Publish standalone single-file x64 build (ReadyToRun) to ./publish/Imm.App.exe
# IncludeNativeLibrariesForSelfExtract pulls the native WPF dlls (wpfgfx, PresentationNative,
# D3DCompiler, vcruntime, PenImc) INTO the bundle; compression trades a little startup time
# for roughly half the size; DebugType=none drops the .pdb files.
set -euo pipefail
cd "$(dirname "$0")/.."

taskkill //IM Imm.App.exe //F 2>/dev/null || true
rm -rf publish
dotnet publish src/Imm.App/Imm.App.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishReadyToRun=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none \
  -o publish --nologo

echo
echo "Done: $(pwd)/publish/Imm.App.exe"
