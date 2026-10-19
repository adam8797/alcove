#!/usr/bin/env bash
# Re-creates all test material: certs (self-signed, test-only), unsigned/signed/tampered packages,
# and downloads the nuget.org packages. Nothing is added to any trust store.
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
rm -rf certs feed && mkdir -p certs feed downloaded

# --- 1. Self-signed code-signing certificates (files only) -------------------------------
mkcert() { # name CN
  openssl req -x509 -newkey rsa:3072 -sha256 -days 365 -nodes \
    -keyout certs/$1.key -out certs/$1.crt -subj "/CN=$2/O=Spike Test" \
    -addext "keyUsage=critical,digitalSignature" -addext "extendedKeyUsage=codeSigning" \
    -addext "basicConstraints=critical,CA:FALSE" 2>/dev/null
  openssl pkcs12 -export -inkey certs/$1.key -in certs/$1.crt -out certs/$1.pfx -passout pass:spike
  echo "$1 SHA-256: $(openssl x509 -in certs/$1.crt -noout -fingerprint -sha256 | cut -d= -f2 | tr -d :)"
}
mkcert author-test "Spike Test Author"
mkcert other-test  "Spike Other Author"
# A test CA (root kept as a file only) and a leaf it issued, for the "custom root" experiment.
openssl req -x509 -newkey rsa:3072 -sha256 -days 365 -nodes -keyout certs/test-root.key -out certs/test-root.crt \
  -subj "/CN=Spike Test Root CA/O=Spike Test" -addext "basicConstraints=critical,CA:TRUE" \
  -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
openssl req -newkey rsa:3072 -nodes -keyout certs/ca-issued.key -out certs/ca-issued.csr -subj "/CN=Spike CA-issued Author/O=Spike Test" 2>/dev/null
printf "keyUsage=critical,digitalSignature\nextendedKeyUsage=codeSigning\nbasicConstraints=critical,CA:FALSE\n" > certs/leaf.ext
openssl x509 -req -in certs/ca-issued.csr -CA certs/test-root.crt -CAkey certs/test-root.key -CAcreateserial \
  -days 300 -sha256 -extfile certs/leaf.ext -out certs/ca-issued.crt 2>/dev/null
cat certs/ca-issued.crt certs/test-root.crt > certs/ca-issued-chain.crt
openssl pkcs12 -export -inkey certs/ca-issued.key -in certs/ca-issued.crt -certfile certs/test-root.crt -out certs/ca-issued.pfx -passout pass:spike
for c in author-test other-test ca-issued test-root; do openssl x509 -in certs/$c.crt -noout -fingerprint -sha256 | cut -d= -f2 | tr -d : > certs/$c.sha256; done

# --- 2. Unsigned package --------------------------------------------------------------------
dotnet pack SamplePlugin -c Release -o feed/unsigned --nologo -v q >/dev/null
U=feed/unsigned/Spike.SamplePlugin.1.0.0.nupkg

# --- 3. Author-sign with the self-signed certs (no timestamper available offline) -----------
sign() { # cert outdir
  mkdir -p feed/$2 && cp $U feed/$2/
  dotnet nuget sign feed/$2/Spike.SamplePlugin.1.0.0.nupkg --certificate-path certs/$1.pfx \
     --certificate-password spike --overwrite 2>&1 | grep -E "NU[0-9]{4}|rror" | sed "s/^/   [sign $1] /" || true
}
sign author-test signed-selfsigned
sign other-test  signed-other
# Negative result: signing with a leaf issued by a private CA (root not in the SDK bundle) fails on Linux
# with NU3018 PartialChain (an error), unlike a self-signed leaf (NU3018 UntrustedRoot = warning).
sign ca-issued   signed-ca-issued
rm -rf feed/signed-ca-issued

# --- 4. Tampered packages (flip one byte inside the DLL entry's data) ----------------------
mkdir -p feed/tampered
cp feed/signed-selfsigned/Spike.SamplePlugin.1.0.0.nupkg feed/tampered/selfsigned-tampered.nupkg
python3 -I tools/tamper.py feed/tampered/selfsigned-tampered.nupkg lib/net10.0/SamplePlugin.dll

# --- 5. nuget.org packages (untrusted data, never executed) --------------------------------
for p in newtonsoft.json/13.0.3 humanizer.core/2.14.1 serilog/4.0.0 dapper/2.1.35; do
  id=${p%/*}; v=${p#*/}; f=downloaded/$id.$v.nupkg
  [ -f $f ] || curl -sSfLo $f https://api.nuget.org/v3-flatcontainer/$id/$v/$id.$v.nupkg
done
[ -f downloaded/nugetorg-repository-signatures.json ] || curl -sSfo downloaded/nugetorg-repository-signatures.json https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json
cp downloaded/newtonsoft.json.13.0.3.nupkg feed/tampered/newtonsoft-tampered.nupkg
python3 -I tools/tamper.py feed/tampered/newtonsoft-tampered.nupkg lib/net6.0/Newtonsoft.Json.dll
find feed downloaded -name '*.nupkg' | sort
