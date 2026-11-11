#!/usr/bin/env bash
# Build the wolfSSL host programs network.robot uses. Usage: host-clients.sh [OUTDIR]
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="${1:-$repo/wolfssl-host}"

rev="$(python3 -c "import yaml; m = yaml.safe_load(open('$repo/west.yml')); print(next(p['revision'] for p in m['manifest']['projects'] if p['name'] == 'wolfssl'))")"
client_src="$(find "$repo" -maxdepth 2 -name client-tls13-filetransfer.c | head -1)"
[ -n "$client_src" ] || { echo "client-tls13-filetransfer.c not found under $repo" >&2; exit 1; }

rm -rf "$out"
git clone -q --depth 1 --branch "$rev" https://github.com/wolfSSL/wolfssl "$out"
cd "$out"
./autogen.sh
./configure --enable-mlkem --enable-dilithium --enable-curve25519 --enable-ed25519 \
    CPPFLAGS=-DWOLFSSL_PQC_HYBRIDS
make -j"$(nproc)"
gcc "$client_src" -o client-tls13-filetransfer -I. -Lsrc/.libs -lwolfssl -lpthread -lm
echo "wolfSSL $rev host clients in $out (export LD_LIBRARY_PATH=$out/src/.libs)"
