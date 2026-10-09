#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
[[ "${LOCAL_RUNTIME:-}" == acropolis-channel-local ]] || { echo 'Local runtime contract required.' >&2; exit 2; }
[[ "${LOCAL_APP_UID:-}" =~ ^[1-9][0-9]*$ ]] || exit 2
: "${LOCAL_DP_PASSWORD:?Local protector password required}"
mkdir -p /local-pki/{authority,web,smtp,trust,protection,crl} /keyring /export
ca=/local-pki/authority
if [[ ! -s "$ca/root.crt" || ! -s "$ca/root.key" ]]; then
  [[ ! -e "$ca/root.crt" && ! -e "$ca/root.key" ]] || { echo 'Incomplete local SMTP authority; preserve and recover it.' >&2; exit 1; }
  openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -keyout "$ca/root.key" -out "$ca/root.crt" \
    -subj '/CN=Acropolis Local SMTP CA' -addext 'basicConstraints=critical,CA:TRUE,pathlen:0' \
    -addext 'keyUsage=critical,keyCertSign,cRLSign' >/dev/null 2>&1
fi
openssl x509 -in "$ca/root.crt" -checkend 604800 -noout >/dev/null || { echo 'Local SMTP authority needs explicit renewal.' >&2; exit 1; }
if [[ ! -s /local-pki/web/localhost.crt || ! -s /local-pki/web/localhost.key ]]; then
  [[ ! -e /local-pki/web/localhost.crt && ! -e /local-pki/web/localhost.key ]] || { echo 'Incomplete local web certificate.' >&2; exit 1; }
  # Trust only this localhost leaf on Windows, never the SMTP authority.
  openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -keyout /local-pki/web/localhost.key -out /local-pki/web/localhost.crt \
    -subj '/CN=Acropolis Localhost Development' -addext 'basicConstraints=critical,CA:FALSE' \
    -addext 'keyUsage=critical,digitalSignature,keyEncipherment' -addext 'extendedKeyUsage=serverAuth' \
    -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1,IP:::1' >/dev/null 2>&1
fi
openssl x509 -in /local-pki/web/localhost.crt -checkend 604800 -noout >/dev/null || { echo 'Localhost certificate needs explicit renewal and trust update.' >&2; exit 1; }
if [[ ! -s /local-pki/smtp/cert.pem || ! -s /local-pki/smtp/key.pem ]] || ! openssl x509 -in /local-pki/smtp/cert.pem -checkend 604800 -noout >/dev/null; then
  openssl req -new -newkey rsa:2048 -nodes -keyout /local-pki/smtp/key.pem -out /tmp/smtp.csr -subj '/CN=mailpit' >/dev/null 2>&1
  cat > /tmp/smtp.ext <<'EXT'
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:mailpit
crlDistributionPoints=URI:http://crl:8082/root.crl
EXT
  openssl x509 -req -in /tmp/smtp.csr -CA "$ca/root.crt" -CAkey "$ca/root.key" -set_serial "0x$(openssl rand -hex 16)" \
    -days 365 -extfile /tmp/smtp.ext -out /local-pki/smtp/cert.pem >/dev/null 2>&1
fi
openssl pkey -in /local-pki/smtp/key.pem -pubout -out /tmp/smtp-key.pub >/dev/null 2>&1
openssl x509 -in /local-pki/smtp/cert.pem -pubkey -noout -out /tmp/smtp-cert.pub
cmp -s /tmp/smtp-key.pub /tmp/smtp-cert.pub || { echo 'Local SMTP certificate and key differ; preserve and repair them.' >&2; exit 1; }
touch "$ca/index.txt"
[[ -s "$ca/crlnumber" ]] || printf '01\n' > "$ca/crlnumber"
cat > /tmp/ca.cnf <<'CRL'
[ca]
default_ca = local_authority
[local_authority]
database = /local-pki/authority/index.txt
certificate = /local-pki/authority/root.crt
private_key = /local-pki/authority/root.key
default_md = sha256
default_crl_days = 3650
crlnumber = /local-pki/authority/crlnumber
crl_extensions = local_crl
[local_crl]
authorityKeyIdentifier = keyid,issuer
CRL
openssl ca -gencrl -config /tmp/ca.cnf -out /local-pki/crl/root.pem >/dev/null 2>&1
openssl crl -in /local-pki/crl/root.pem -outform DER -out /local-pki/crl/root.crl
cp "$ca/root.crt" /local-pki/trust/root.crt
cat /etc/ssl/certs/ca-certificates.crt "$ca/root.crt" > /local-pki/trust/ca-bundle.crt
openssl verify -crl_check -CAfile "$ca/root.crt" -CRLfile /local-pki/crl/root.pem -verify_hostname mailpit /local-pki/smtp/cert.pem >/dev/null
if [[ ! -s /local-pki/protection/protector.pfx ]]; then
  [[ ! -e /local-pki/protection/protector.pfx && -z "$(find /keyring -maxdepth 1 -name 'key-*.xml' -print -quit)" ]] || { echo 'Existing keys require their original local protector.' >&2; exit 1; }
  openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -keyout /tmp/protector.key -out /tmp/protector.crt \
    -subj '/CN=Acropolis Local Data Protection' >/dev/null 2>&1
  openssl pkcs12 -export -inkey /tmp/protector.key -in /tmp/protector.crt -out /local-pki/protection/protector.pfx -passout env:LOCAL_DP_PASSWORD
fi
openssl pkcs12 -in /local-pki/protection/protector.pfx -passin env:LOCAL_DP_PASSWORD -info -noout >/dev/null 2>&1 || { echo 'Local protector password or file is invalid; preserve it.' >&2; exit 1; }
chown -R "$LOCAL_APP_UID:$LOCAL_APP_UID" /keyring /local-pki/protection
chmod 750 /keyring /local-pki/protection
chmod 640 /local-pki/protection/protector.pfx
chmod 755 /local-pki /local-pki/{web,smtp,trust,crl}
chmod 644 /local-pki/web/localhost.crt /local-pki/smtp/cert.pem /local-pki/trust/*.crt /local-pki/crl/*
chmod 600 /local-pki/web/localhost.key /local-pki/smtp/key.pem "$ca/root.key"
cp /local-pki/web/localhost.crt /export/localhost.crt
chmod 644 /export/localhost.crt
echo 'Local HTTPS, TLS mail sink and persistent Data Protection material ready.'
