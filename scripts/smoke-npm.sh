#!/usr/bin/env bash
# Verifies npm install resolves both metadata and tarball through Packory only.
set -euo pipefail
BASE_URL="${PACKORY_PUBLIC_BASE_URL:-http://localhost:8080}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"
printf '{"name":"packory-npm-smoke","version":"0.0.1","private":true}\n' > package.json
npm install 'is-number@7.0.0' --registry="${BASE_URL}/npm/" --ignore-scripts --no-audit --no-fund --cache "$WORK/cache"
node -e 'if (!require("is-number")(42)) process.exit(1)'
node -e '
const fs=require("node:fs");
const lock=JSON.parse(fs.readFileSync("package-lock.json","utf8"));
const url=lock.packages["node_modules/is-number"].resolved;
if (!url.startsWith(process.env.PACKORY_PUBLIC_BASE_URL || "http://localhost:8080")) {
  console.error("Install bypassed Packory:",url); process.exit(1);
}
'
printf 'npm live mirror smoke succeeded (is-number 7.0.0)\n'
