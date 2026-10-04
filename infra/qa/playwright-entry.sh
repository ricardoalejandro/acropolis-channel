#!/usr/bin/env bash
set -Eeuo pipefail
[[ "${QA_PROJECT:-}" =~ ^acropolis_test_[a-z0-9_]+$ ]] || exit 2
[[ -s /qa-trust/root.crt ]] || { echo 'Missing QA trust root.' >&2; exit 1; }
mkdir -p "$HOME/.pki/nssdb"
certutil -N --empty-password -d "sql:$HOME/.pki/nssdb"
certutil -A -d "sql:$HOME/.pki/nssdb" -t 'C,,' -n 'Acropolis isolated QA root' -i /qa-trust/root.crt
export NODE_EXTRA_CA_CERTS=/qa-trust/root.crt
exec "$@"
