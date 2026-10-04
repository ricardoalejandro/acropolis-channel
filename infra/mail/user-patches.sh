#!/bin/bash
set -euo pipefail
# Never trust a Docker subnet to relay. Authenticated submission only.
postconf -e 'mynetworks=127.0.0.0/8'
postconf -e 'default_transport=smtp' 'relay_transport=smtp' 'smtp_transport_rate_delay=16s' 'smtp_destination_recipient_limit=1'
