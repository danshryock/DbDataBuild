#!/usr/bin/env bash
# One self-contained file for a platform: the .NET runtime, the tool, and the native libraries (polyglot, DuckDB), which the runtime
# unpacks on first start into DOTNET_BUNDLE_EXTRACT_BASE_DIR (default ~/.net).
#   scripts/publish.sh [rid] [outdir]      rid defaults to linux-x64, outdir to ./publish/<rid>
# The polyglot library for the rid must exist in native/<rid>/ (scripts/build-polyglot.sh). Only linux-x64 has been built and run so far.
set -euo pipefail
cd "$(dirname "$0")/.."
RID="${1:-linux-x64}"
OUT="${2:-publish/$RID}"
[ -d "native/$RID" ] || { echo "native/$RID is missing: build polyglot for $RID first (scripts/build-polyglot.sh)." >&2; exit 1; }
dotnet publish src/DbDataBuild.Cli -c Release -r "$RID" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true \
  -p:DebugType=none -p:DebugSymbols=false -o "$OUT"
ls -l "$OUT"
