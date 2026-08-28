#!/usr/bin/env bash
# Canonical IMM verification (IMM project): Release build with zero warnings + all tests green.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet build Imm.sln -c Release -warnaserror --nologo
dotnet test Imm.sln -c Release --no-build --nologo
