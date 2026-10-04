#!/usr/bin/env python3
"""Prepare or validate private Identity runtime material without starting services."""
import argparse
import ipaddress
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
DOMAIN = "https://acropolischannel.naperu.cloud"
APP_UID = 1654


def read_settings():
    values = {}
    for line in (ROOT / ".env").read_text().splitlines():
        if line.strip() and not line.lstrip().startswith("#") and "=" in line:
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip().strip('"').strip("'")
    return values


def write_settings(changes):
    path = ROOT / ".env"
    if path.is_symlink():
        raise RuntimeError("Private configuration must not be a symbolic link")
    lines = path.read_text().splitlines()
    retained = [line for line in lines if line.split("=", 1)[0].strip() not in changes]
    retained.extend(key + "=" + value for key, value in changes.items())
    temporary = path.with_name(".env.identity.tmp")
    with temporary.open("x") as handle:
        os.chmod(temporary, 0o600)
        handle.write("\n".join(retained) + "\n")
    temporary.replace(path)


def command(arguments, *, environment=None):
    result = subprocess.run(arguments, text=True, capture_output=True, env=environment, check=False)
    if result.returncode:
        raise RuntimeError("Identity runtime command failed; private output suppressed")
    return result.stdout.strip()


def discover_proxies(network):
    containers = command(["docker", "ps", "--filter",
                          "label=com.docker.swarm.service.name=dokploy-traefik", "-q"]).splitlines()
    if not containers:
        raise RuntimeError("No running Dokploy Traefik task was found")
    addresses = set()
    for container in containers:
        data = json.loads(command(["docker", "inspect", container]))[0]
        attachment = data.get("NetworkSettings", {}).get("Networks", {}).get(network)
        if attachment and attachment.get("IPAddress"):
            addresses.add(str(ipaddress.ip_address(attachment["IPAddress"])))
    if not addresses:
        raise RuntimeError("Traefik has no address on the configured publication network")
    return ",".join(sorted(addresses))


def validate_settings(settings):
    required = ["IDENTITY_DP_CERT_PASSWORD", "IDENTITY_KNOWN_PROXIES",
                "IDENTITY_SMTP_HOST", "IDENTITY_SMTP_FROM_EMAIL"]
    missing = [key for key in required if not settings.get(key)]
    if missing:
        raise RuntimeError("Missing private settings: " + ", ".join(missing))
    if settings.get("IDENTITY_PUBLIC_ORIGIN", DOMAIN) != DOMAIN:
        raise RuntimeError("Identity public origin must match the configured project domain")
    for address in settings["IDENTITY_KNOWN_PROXIES"].split(","):
        parsed = ipaddress.ip_address(address.strip())
        if parsed.is_unspecified or parsed.is_multicast:
            raise RuntimeError("Invalid trusted proxy address")
    try:
        port = int(settings.get("IDENTITY_SMTP_PORT", "587"))
    except ValueError:
        raise RuntimeError("SMTP port must be numeric") from None
    if not 1 <= port <= 65535:
        raise RuntimeError("SMTP port is outside the valid range")
    if settings.get("IDENTITY_SMTP_SECURITY", "starttls") not in ("starttls", "ssl"):
        raise RuntimeError("Production SMTP requires STARTTLS or TLS")
    if bool(settings.get("IDENTITY_SMTP_USERNAME")) != bool(settings.get("IDENTITY_SMTP_PASSWORD")):
        raise RuntimeError("SMTP username and password must be configured together")
    sender = settings["IDENTITY_SMTP_FROM_EMAIL"]
    if sender.count("@") != 1 or any(character.isspace() for character in sender):
        raise RuntimeError("SMTP sender must be an email address")


def prepare():
    settings = read_settings()
    folder = ROOT / ".local" / "identity"
    if folder.is_symlink():
        raise RuntimeError("Identity material directory must not be a symbolic link")
    folder.mkdir(parents=True, exist_ok=True)
    folder.chmod(0o700)
    certificate = folder / "key-protector.pfx"
    if certificate.is_symlink():
        raise RuntimeError("Protector must not be a symbolic link")
    password = settings.get("IDENTITY_DP_CERT_PASSWORD")
    if certificate.exists() and not password:
        raise RuntimeError("Existing protector has no configured password; recovery required")
    if password and not certificate.exists():
        raise RuntimeError("Configured protector is missing; restore it instead of replacing keys")
    if not certificate.exists():
        password = secrets.token_urlsafe(48)
        environment = dict(os.environ, ACROPOLIS_PFX_PASSWORD=password)
        private_key = folder / "protector-key.pending"
        public_certificate = folder / "protector-cert.pending"
        pending = folder / "key-protector.pfx.pending"
        if any(path.exists() for path in (private_key, public_certificate, pending)):
            raise RuntimeError("Pending protector files exist; review them before preparing again")
        previous_mask = os.umask(0o077)
        try:
            command(["openssl", "req", "-x509", "-newkey", "rsa:3072", "-sha256",
                     "-days", "3650", "-nodes", "-subj", "/CN=Acropolis Data Protection",
                     "-keyout", str(private_key), "-out", str(public_certificate)])
            command(["openssl", "pkcs12", "-export", "-out", str(pending),
                     "-inkey", str(private_key), "-in", str(public_certificate),
                     "-passout", "env:ACROPOLIS_PFX_PASSWORD"], environment=environment)
            pending.chmod(0o640)
            os.chown(pending, 0, APP_UID)
            # Record the password before exposing the new certificate path.
            write_settings({"IDENTITY_DP_CERT_PASSWORD": password})
            pending.replace(certificate)
        finally:
            os.umask(previous_mask)
            private_key.unlink(missing_ok=True)
            public_certificate.unlink(missing_ok=True)
    proxies = discover_proxies(settings.get("TRAEFIK_NETWORK", "dokploy-network"))
    write_settings({"IDENTITY_KNOWN_PROXIES": proxies, "IDENTITY_PUBLIC_ORIGIN": DOMAIN})
    print("Private protector and trusted proxies prepared. SMTP still requires validation.")


def validate_protector(certificate, password):
    if not certificate.is_file() or certificate.is_symlink():
        raise RuntimeError("Private key protector is missing")
    attributes = certificate.stat()
    if attributes.st_mode & 0o027:
        raise RuntimeError("Protector must not grant other users access or group write permission")
    readable = ((attributes.st_uid == APP_UID and attributes.st_mode & 0o400)
                or (attributes.st_gid == APP_UID and attributes.st_mode & 0o040))
    if not readable:
        raise RuntimeError("Protector is not readable by the application identity; restore its private ownership and permissions")
    environment = dict(os.environ, ACROPOLIS_PFX_PASSWORD=password, LC_ALL="C")
    # -noout emits bag metadata only, never key/certificate material.
    result = subprocess.run(
        ["openssl", "pkcs12", "-in", str(certificate), "-info", "-noout",
         "-passin", "env:ACROPOLIS_PFX_PASSWORD"],
        text=True, capture_output=True, env=environment, check=False)
    if result.returncode or "Shrouded Keybag" not in result.stderr:
        raise RuntimeError("Protector must contain an encrypted private key and accept its configured password")


def check():
    settings = read_settings()
    validate_settings(settings)
    certificate = ROOT / ".local" / "identity" / "key-protector.pfx"
    validate_protector(certificate, settings["IDENTITY_DP_CERT_PASSWORD"])
    actual = discover_proxies(settings.get("TRAEFIK_NETWORK", "dokploy-network"))
    configured = ",".join(sorted(str(ipaddress.ip_address(value.strip()))
                                 for value in settings["IDENTITY_KNOWN_PROXIES"].split(",")))
    if configured != actual:
        raise RuntimeError("Trusted proxy addresses changed; run identity-runtime.py --prepare")
    print("Identity runtime configuration validated. SMTP delivery requires a real delivery check.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--prepare", action="store_true")
    action.add_argument("--check", action="store_true")
    arguments = parser.parse_args()
    prepare() if arguments.prepare else check()


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("Identity runtime preparation stopped: " + str(error), file=sys.stderr)
        sys.exit(1)
