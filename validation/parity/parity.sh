#!/usr/bin/env bash
# Parity of the C# storm design with the web build's design.ts: every parameter of every frame (step 1.7 min) of all 7
# presets, the teaching sequence and three seeds. Needs node, the website checkout next to this repo
# (../StormLabWebsite with its node_modules) and Unity's bundled .NET SDK.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
web="$here/../../../StormLabWebsite"
out="${TMPDIR:-/tmp}/stormlab-parity"; mkdir -p "$out"
"$web/node_modules/.bin/esbuild" "$here/dump_web.ts" --bundle --platform=node --format=esm --outfile="$out/dump_web.mjs" --log-level=warning
node "$out/dump_web.mjs" > "$out/web.json"
DOTNET="${DOTNET:-/c/Program Files/Unity/Hub/Editor/6000.6.6f1/Editor/Data/DotNetSdk/dotnet.exe}"
"$DOTNET" run -c Release --project "$here/csharp/Parity.csproj" > "$out/cs.json"
python -I "$here/compare.py" "$out/web.json" "$out/cs.json"
