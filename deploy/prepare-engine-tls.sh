#!/usr/bin/env sh
# Run from the repository root. Publish both service identities together, with private permissions.
set -eu
umask 077
dest=deploy/secrets
if [ -s "$dest/api.env" ] && [ -s "$dest/engine.env" ]; then exit 0; fi
if [ -e "$dest" ]; then
    echo "Incomplete engine TLS identities in $dest; restore the pair or move it aside before provisioning." >&2
    exit 1
fi
tmp=$(mktemp -d deploy/.tls-XXXXXX)
trap 'rm -f "$tmp"/*.key "$tmp"/*.crt "$tmp"/*.p12 "$tmp"/api.env "$tmp"/engine.env; rmdir "$tmp" 2>/dev/null || true' EXIT
for name in engine api; do
    usage=serverAuth
    if [ "$name" = api ]; then usage=clientAuth; fi
    MSYS2_ARG_CONV_EXCL='*' openssl req -x509 -newkey rsa:3072 -nodes -sha256 -days 365 \
        -subj "/CN=auction-$name" -addext 'keyUsage=critical,digitalSignature,keyEncipherment' \
        -addext "extendedKeyUsage=$usage" -keyout "$tmp/$name.key" -out "$tmp/$name.crt"
    openssl pkcs12 -export -inkey "$tmp/$name.key" -in "$tmp/$name.crt" -passout pass: -out "$tmp/$name.p12"
done
for name in engine api; do
    peer=engine
    if [ "$name" = engine ]; then peer=api; fi
    {
        printf 'EngineTls__Identity='
        openssl base64 -A -in "$tmp/$name.p12"
        printf '\nEngineTls__PeerSha256='
        openssl x509 -in "$tmp/$peer.crt" -noout -sha256 -fingerprint | cut -d= -f2 | tr -d ':\r'
    } > "$tmp/$name.env"
done
rm -f "$tmp"/*.key "$tmp"/*.crt "$tmp"/*.p12
mv "$tmp" "$dest"
