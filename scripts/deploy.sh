#!/usr/bin/env bash
set -euo pipefail

cd /root/proyect/acropolis-channel
git pull --ff-only origin main
docker compose -p acropolis-channel up -d --build
