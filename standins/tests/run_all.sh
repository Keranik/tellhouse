#!/bin/sh
# Full check run for the stand-ins: build, both stand-in vector selftests,
# and the live protocol driver against the real relay.
# No live model API is used. Spend: $0.
set -e
cd "$(dirname "$0")/.."
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
for p in LisaAssistant DanielAssistant MockNode; do
  dotnet build "$p" -v q -nologo
done
VECTORS_PATH="$PWD/vectors/test-vectors.json" ./LisaAssistant/bin/Debug/net10.0/LisaAssistant selftest
VECTORS_PATH="$PWD/vectors/test-vectors.json" ./DanielAssistant/bin/Debug/net10.0/DanielAssistant selftest
python3 tests/run_protocol_checks.py
