#!/usr/bin/env bash
# Runs every experiment; output goes to results/*.txt. Requires ./setup.sh to have run.
# Offline runs use `unshare -rn` (unprivileged user+network namespace: no network at all, no sudo).
# Every run uses a throw-away HOME so .NET's CRL cache (~/.dotnet/corefx/cryptography/crls) starts cold
# unless stated otherwise.
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
DOTNET="$(command -v dotnet)"
mkdir -p results out
dotnet build Verifier -c Release -o out/plain -v q --nologo | grep -E "rror\(s\)"
dotnet build Verifier -c Release -p:BundleTrustedRoots=true -o out/bundled -v q --nologo | grep -E "rror\(s\)"
PLAIN="$DOTNET out/plain/Verifier.dll"; BUNDLED="$DOTNET out/bundled/Verifier.dll"
newhome() { local h; h=$(mktemp -d "${TMPDIR:-/tmp}/sigspike-home.XXXX"); echo "$h"; }
offline() { unshare -rn "$@"; }

H=$(newhome); HOME=$H $BUNDLED defaults                                      > results/defaults.txt
H=$(newhome); HOME=$H $PLAIN   inspect downloaded/*.nupkg feed/*/*.nupkg --diag > results/inspect.txt 2>&1

# Q2: which trust store
H=$(newhome); HOME=$H $PLAIN   matrix --trust-store default --diag          > results/matrix-plain-default-online.txt 2>&1
H=$(newhome); HOME=$H $PLAIN   matrix --trust-store sdk                     > results/matrix-plain-sdk-nobundle-online.txt 2>&1
H=$(newhome); HOME=$H $BUNDLED matrix --trust-store default                 > results/matrix-bundled-default-online.txt 2>&1
WARM=$(newhome); HOME=$WARM $BUNDLED matrix --trust-store sdk --warnings    > results/matrix-bundled-sdk-online.txt 2>&1

# Q5: offline
H=$(newhome); HOME=$H offline $BUNDLED matrix --trust-store sdk --warnings                      > results/matrix-bundled-sdk-OFFLINE-revOnline-coldcache.txt 2>&1
H=$(newhome); HOME=$H offline $BUNDLED matrix --trust-store sdk --warnings --revocation offline  > results/matrix-bundled-sdk-OFFLINE-revOffline-coldcache.txt 2>&1
HOME=$WARM offline $BUNDLED matrix --trust-store sdk --warnings                                 > results/matrix-bundled-sdk-OFFLINE-revOnline-warmcache.txt 2>&1
# The spike doc's suggestion: unreachable proxy instead of no network
H=$(newhome); HOME=$H HTTP_PROXY=http://127.0.0.1:9 HTTPS_PROXY=http://127.0.0.1:9 $BUNDLED matrix --trust-store sdk --warnings > results/matrix-bundled-sdk-DEADPROXY-revOnline-coldcache.txt 2>&1

# Q7: timing (cold process each; 30 iterations)
: > results/timing.txt
for p in downloaded/newtonsoft.json.13.0.3.nupkg downloaded/serilog.4.0.0.nupkg feed/signed-selfsigned/Spike.SamplePlugin.1.0.0.nupkg feed/unsigned/Spike.SamplePlugin.1.0.0.nupkg; do
  H=$(newhome); echo "--- online, cold CRL cache: $p" >> results/timing.txt; HOME=$H $BUNDLED time $p 30 --trust-store sdk 2>&1 | grep -v "^#" >> results/timing.txt
  echo "--- offline, revocation=offline: $p" >> results/timing.txt; HOME=$H offline $BUNDLED time $p 30 --trust-store sdk --revocation offline 2>&1 | grep -v "^#" >> results/timing.txt
done
H=$(newhome); echo "--- OFFLINE, revocation=online, cold cache: newtonsoft" >> results/timing.txt; HOME=$H offline $BUNDLED time downloaded/newtonsoft.json.13.0.3.nupkg 5 --trust-store sdk 2>&1 | grep -v "^#" >> results/timing.txt
# Q4: NuGet's own <trustedSigners> model via ClientPolicyContext
H=$(newhome); HOME=$H $BUNDLED nugetconfig --trust-store sdk > results/nugetconfig-trustedsigners.txt 2>&1
# Q5: strict revocation (AllowUnknownRevocation=false) while offline
H=$(newhome); { echo "--- offline, revocation=online, strict"; HOME=$H offline $BUNDLED time downloaded/newtonsoft.json.13.0.3.nupkg 2 --trust-store sdk --strict-revocation;
  echo "--- offline, revocation=offline, strict"; HOME=$H offline $BUNDLED time downloaded/newtonsoft.json.13.0.3.nupkg 2 --trust-store sdk --revocation offline --strict-revocation; } 2>&1 | grep -v "^#" | cut -c1-400 > results/strict-revocation-offline.txt
# Q2: single-file publish
dotnet publish Verifier -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:BundleTrustedRoots=true -o out/singlefile -v q --nologo >/dev/null
dotnet publish Verifier -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:BundleTrustedRoots=true -p:KeepNuGetPackagingOnDisk=true -o out/singlefile-fixed -v q --nologo >/dev/null
{ echo "--- single-file, NuGet.Packaging.dll embedded"; out/singlefile/Verifier matrix --trust-store sdk 2>&1 | head -6 || true
  echo "--- single-file, NuGet.Packaging.dll excluded from bundle"; out/singlefile-fixed/Verifier matrix --trust-store sdk 2>&1 | sed -n '1,3p;/## P1/,/## P2/p' | grep -E "^#|ACCEPT|REJECT"; } > results/singlefile.txt 2>&1
# Q5/Q7: hanging network (slow: ~6 min). Opt-in with SLOW=1.
if [ "${SLOW:-0}" = 1 ]; then
  python3 -I tools/blackhole.py 18999 & BH=$!
  H=$(newhome); { echo "--- hanging proxy, revocation=online"; HOME=$H HTTP_PROXY=http://127.0.0.1:18999 HTTPS_PROXY=http://127.0.0.1:18999 $BUNDLED time downloaded/newtonsoft.json.13.0.3.nupkg 3 --trust-store sdk
    echo "--- hanging proxy, revocation=online, CancellationToken 5s"; HOME=$H HTTP_PROXY=http://127.0.0.1:18999 $BUNDLED time downloaded/newtonsoft.json.13.0.3.nupkg 1 --trust-store sdk --timeout-ms 5000
    echo "--- hanging proxy, revocation=offline"; HOME=$H HTTP_PROXY=http://127.0.0.1:18999 HTTPS_PROXY=http://127.0.0.1:18999 $BUNDLED time downloaded/newtonsoft.json.13.0.3.nupkg 3 --trust-store sdk --revocation offline; } 2>&1 | grep -v "^#" | cut -c1-300 > results/timing-hanging-network.txt
  kill $BH
fi
rm -rf "${TMPDIR:-/tmp}"/sigspike-home.*
echo done; ls -1 results
