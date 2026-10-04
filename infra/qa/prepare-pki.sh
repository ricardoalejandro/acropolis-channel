#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
[[ "${QA_PROJECT:-}" =~ ^acropolis_test_[a-z0-9_]+$ ]] || { echo 'PKI initialization requires an isolated QA run.' >&2; exit 2; }
[[ "${QA_APP_UID:-}" =~ ^[1-9][0-9]*$ ]] || exit 2
case "${1:-prepare}" in
  prepare)
    : "${QA_DP_PASSWORD:?QA protector password required}"
    for attempt in {1..60}; do
      [[ -s /caddy-data/caddy/pki/authorities/local/root.crt && -s /caddy-data/caddy/pki/authorities/local/root.key ]] && break
      sleep 1
    done
    ca=/caddy-data/caddy/pki/authorities/local
    [[ -s "$ca/root.crt" && -s "$ca/root.key" ]] || { echo 'Isolated Caddy CA did not become available.' >&2; exit 1; }
    mkdir -p /tls/trust /tls/smtp /tls/protection /tls/crl
    cp "$ca/root.crt" /tls/trust/root.crt
    cat /etc/ssl/certs/ca-certificates.crt "$ca/root.crt" > /tls/trust/ca-bundle.crt
    openssl req -new -newkey rsa:2048 -nodes -keyout /tls/smtp/key.pem -out /tls/smtp/request.csr -subj '/CN=mailpit' >/dev/null 2>&1
    cat > /tls/smtp/extensions <<'EXT'
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:mailpit,DNS:mailpit-starttls
crlDistributionPoints=URI:http://crl:8082/root.crl
EXT
    openssl x509 -req -in /tls/smtp/request.csr -CA "$ca/root.crt" -CAkey "$ca/root.key" -set_serial "0x$(openssl rand -hex 16)" -days 1 -extfile /tls/smtp/extensions -out /tls/smtp/cert.pem >/dev/null 2>&1
    mkdir -p /tmp/qa-crl
    touch /tmp/qa-crl/index.txt
    printf '01\n' > /tmp/qa-crl/crlnumber
    cat > /tmp/qa-crl/openssl.cnf <<'CRL'
[ca]
default_ca = qa_authority
[qa_authority]
database = /tmp/qa-crl/index.txt
certificate = /caddy-data/caddy/pki/authorities/local/root.crt
private_key = /caddy-data/caddy/pki/authorities/local/root.key
default_md = sha256
default_crl_days = 1
crlnumber = /tmp/qa-crl/crlnumber
crl_extensions = qa_crl
[qa_crl]
authorityKeyIdentifier = keyid,issuer
CRL
    openssl ca -gencrl -config /tmp/qa-crl/openssl.cnf -out /tls/crl/root.pem >/dev/null 2>&1
    openssl crl -in /tls/crl/root.pem -outform DER -out /tls/crl/root.crl
    openssl verify -crl_check -CAfile /tls/trust/root.crt -CRLfile /tls/crl/root.pem -verify_hostname mailpit /tls/smtp/cert.pem >/dev/null
    openssl verify -crl_check -CAfile /tls/trust/root.crt -CRLfile /tls/crl/root.pem -verify_hostname mailpit-starttls /tls/smtp/cert.pem >/dev/null
    rm -rf -- /tmp/qa-crl
    openssl req -x509 -newkey rsa:2048 -nodes -days 2 -keyout /tls/protection/key.pem -out /tls/protection/cert.pem -subj '/CN=Acropolis QA Data Protection' >/dev/null 2>&1
    openssl pkcs12 -export -inkey /tls/protection/key.pem -in /tls/protection/cert.pem -out /tls/protection/protector.pfx -passout env:QA_DP_PASSWORD
    rm -- /tls/smtp/request.csr /tls/smtp/extensions /tls/protection/key.pem /tls/protection/cert.pem
    chmod 755 /tls/trust /tls/smtp /tls/crl
    chmod 644 /tls/trust/*.crt /tls/smtp/cert.pem /tls/crl/root.pem /tls/crl/root.crl
    chmod 600 /tls/smtp/key.pem
    chown -R "$QA_APP_UID:$QA_APP_UID" /tls/protection /keyring
    chmod 750 /tls/protection /keyring
    chmod 640 /tls/protection/protector.pfx
    echo 'QA CA, SMTP certificate, signed revocation list and Data Protection protector initialized.'
    ;;
  backup-caddy)
    [[ ! -e /artifacts/caddy-pki.tar ]] || exit 2
    tar -C /caddy-data -cf /artifacts/caddy-pki.tar .
    chmod 600 /artifacts/caddy-pki.tar
    echo 'Private QA CA snapshot created.'
    ;;
  restore-caddy)
    [[ -f /artifacts/caddy-pki.tar && -z "$(find /caddy-data -mindepth 1 -print -quit)" ]] || { echo 'CA restore requires a fresh isolated QA volume.' >&2; exit 2; }
    tar -C /caddy-data -xf /artifacts/caddy-pki.tar
    echo 'QA CA restored only into this run recovery proxy.'
    ;;
  backup-keyring)
    [[ ! -e /artifacts/dp-keyring.tar ]] || exit 2
    [[ -n "$(find /keyring -maxdepth 1 -name 'key-*.xml' -print -quit)" ]] || { echo 'Missing persisted Data Protection keys.' >&2; exit 1; }
    for key in /keyring/key-*.xml; do
      grep -q '<encryptedSecret' "$key" || { echo 'Production candidate persisted an unprotected Data Protection key.' >&2; exit 1; }
    done
    tar -C /keyring -cf /artifacts/dp-keyring.tar .
    chmod 600 /artifacts/dp-keyring.tar
    echo 'QA key ring snapshot created.'
    ;;
  restore-keyring)
    [[ -f /artifacts/dp-keyring.tar && -z "$(find /keyring -mindepth 1 -print -quit)" ]] || { echo 'Key ring restore requires an empty QA volume and a snapshot.' >&2; exit 2; }
    tar -C /keyring -xf /artifacts/dp-keyring.tar
    chown -R "$QA_APP_UID:$QA_APP_UID" /keyring
    chmod 750 /keyring
    echo 'Key ring restored only into a fresh QA volume.'
    ;;
  *) echo 'Unknown QA PKI operation.' >&2; exit 2 ;;
esac
