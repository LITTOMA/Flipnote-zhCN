#!/bin/sh
set -eu
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
exec dotnet run --project "$ROOT/src/Flipnote.Bootstrap/Flipnote.Bootstrap.csproj" -c Release --verbosity quiet -- build "$@"
