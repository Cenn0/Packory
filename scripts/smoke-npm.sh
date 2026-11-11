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
const raw=lock.packages?.["node_modules/is-number"]?.resolved;
const base=process.env.PACKORY_PUBLIC_BASE_URL || "http://localhost:8080";
let sameRegistry=false;
try {
  const trusted=new URL(base.replace(/\\/+$/, "") + "/npm/");
  const candidate=new URL(raw);
  // Comparing string prefixes is insufficient: localhost:8080.evil.invalid
  // would pass despite resolving to an unrelated host.
  sameRegistry=(candidate.origin === trusted.origin &&
    candidate.pathname.startsWith(trusted.pathname) &&
    candidate.username === "" && candidate.password === "" &&
    candidate.search === "" && candidate.hash === "");
} catch {} // Invalid/untrusted URLs are rejected; never echo input to public CI.
if (!sameRegistry) {
  console.error("Install bypassed Packory; resolved source withheld from public logs."); process.exit(1);
}
'
printf 'npm live mirror smoke succeeded (is-number 7.0.0)\n'
