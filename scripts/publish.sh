#!/usr/bin/env bash
# Publish standalone single-file x64 build (ReadyToRun) to ./publish/Imm.App.exe
set -euo pipefail
cd "$(dirname "$0")/.."

taskkill //IM Imm.App.exe //F 2>/dev/null || true
dotnet publish src/Imm.App/Imm.App.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishReadyToRun=true \
  -o publish --nologo

echo
echo "Done: $(pwd)/publish/Imm.App.exe"
