#!/usr/bin/env python3
"""Private host operations for the naperu-mail stack; never modifies Traefik."""
import argparse
import base64
import contextlib
import datetime as dt
import fcntl
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import posixpath
import re
import secrets
import shutil
import smtplib
import socket
import ssl
import stat
import subprocess
import sys
import tarfile
import tempfile
import time

ROOT = Path("/root/proyect/naperu-mail")
ACME = Path("/etc/dokploy/traefik/dynamic/acme.json")
HOST = "mail.naperu.cloud"
WEBMAIL = "webmail.naperu.cloud"
IP = "72.61.37.46"
RESOLVERS = ("8.8.8.8", "1.1.1.1")
PTR_LEGACY = "srv1095953.hstgr.cloud"
PTR_ZONE = "37.61.72.in-addr.arpa"
PTR_AUTHORITIES = ("rdns1.hostinger.com", "rdns2.hostinger.com")
CONTAINER = "naperu-mail-mailserver-1"
CONTAINERS = ("naperu-mail-mailserver-1", "naperu-mail-roundcube-1")
COMPONENTS = ("config", "certs", "mail-data", "mail-state", "roundcube-db",
              "roundcube-config", "roundcube-trust", "compose.yml", "stage.yml",
              "public.yml", "runtime.env", ".local/credentials.json")
# Only known runtime IPC endpoints may be omitted. Regular files at these paths
# are always backed up; unexpected sockets/FIFOs and every device are rejected.
RUNTIME_OMISSIONS = {
    **{"mail-state/spool-postfix/private/" + name: "socket" for name in (
        "scache", "smtpd", "smtp", "error", "retry", "anvil", "bounce", "tlsproxy",
        "local", "lmtp", "proxywrite", "verify", "virtual", "rewrite", "dnsblog",
        "trace", "defer", "tlsmgr", "proxymap", "relay", "discard")},
    **{"mail-state/spool-postfix/public/" + name: "socket" for name in (
        "qmgr", "postlog", "cleanup", "sender-cleanup", "flush", "showq")},
    "mail-state/spool-postfix/public/pickup": "fifo",
    "mail-state/lib-rspamd/rspamd.sock": "socket",
}

CERT_PATH = "/run/naperu-mail/certs/fullchain.pem"
KEY_PATH = "/run/naperu-mail/certs/privkey.pem"
PEM_CERT = re.compile(br"-----BEGIN CERTIFICATE-----\s+.*?-----END CERTIFICATE-----", re.S)


class OpsError(RuntimeError):
    pass


def run(args, *, input=None, timeout=120):
    try:
        result = subprocess.run(args, input=input, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise OpsError("No se pudo ejecutar una herramienta requerida.") from exc
    if result.returncode:
        # stderr can contain credentials, subjects or decrypted paths.
        raise OpsError("La herramienta requerida rechazó la operación.")
    return result.stdout


def private_dir(path):
    path = Path(path)
    if path.is_symlink():
        raise OpsError("Un directorio privado no puede ser un enlace.")
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    if path.stat().st_uid != os.geteuid() or not path.is_dir():
        raise OpsError("Propietario inválido del directorio privado.")
    path.chmod(0o700)


def private_file(path):
    path = Path(path)
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid():
        raise OpsError("El archivo privado debe ser regular y del propietario actual.")
    if stat.S_IMODE(info.st_mode) != 0o600:
        raise OpsError("El archivo privado debe tener permisos 600.")
    return path


def fsync_dir(path):
    fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def atomic_private(path, content):
    path = Path(path)
    fd, name = tempfile.mkstemp(prefix=".new-", dir=path.parent)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, "wb") as handle:
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(name, path)
        fsync_dir(path.parent)
    finally:
        if os.path.exists(name):
            os.unlink(name)


@contextlib.contextmanager
def locked(root):
    private_dir(root / ".local")
    path = root / ".local/mail-ops.lock"
    fd = os.open(path, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        os.fchmod(fd, 0o600)
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    except BlockingIOError as exc:
        raise OpsError("Otra operación de correo mantiene el bloqueo.") from exc
    finally:
        os.close(fd)


def validate_cert(fullchain, key, directory, *, cafile=None, minimum_seconds=604800):
    certs = PEM_CERT.findall(fullchain)
    if not certs or PEM_CERT.sub(b"", fullchain).strip():
        raise OpsError("Cadena PEM inválida.")
    leaf = directory / "leaf.pem"
    chain = directory / "chain.pem"
    privkey = directory / "privkey.pem"
    atomic_private(leaf, certs[0] + b"\n")
    atomic_private(chain, b"\n".join(certs[1:]) + b"\n")
    atomic_private(privkey, key)
    san = run(["openssl", "x509", "-in", str(leaf), "-noout",
               "-ext", "subjectAltName"]).decode("ascii", errors="replace")
    if HOST not in re.findall(r"DNS:([^,\s]+)", san):
        raise OpsError("El certificado no tiene el SAN exacto del correo.")
    run(["openssl", "x509", "-in", str(leaf), "-noout", "-checkend",
         str(minimum_seconds)])
    public = run(["openssl", "x509", "-in", str(leaf), "-pubkey", "-noout"])
    cert_key = run(["openssl", "pkey", "-pubin", "-outform", "DER"], input=public)
    private_key = run(["openssl", "pkey", "-in", str(privkey), "-passin", "pass:", "-pubout",
                       "-outform", "DER"])
    if cert_key != private_key:
        raise OpsError("La clave no corresponde al certificado.")
    args = ["openssl", "verify", "-purpose", "sslserver", "-verify_hostname", HOST]
    if cafile is not None:  # Internal synthetic tests only; CLI never exposes a CA override.
        args += ["-CAfile", str(cafile), "-no-CApath", "-no-CAstore"]
    if len(certs) > 1:
        args += ["-untrusted", str(chain)]
    run(args + [str(leaf)])
    atomic_private(directory / "fullchain.pem", b"\n".join(certs) + b"\n")
    leaf.unlink()
    chain.unlink()


def container_state(name, *, missing_ok=False):
    result = subprocess.run(
        ["docker", "inspect", "--format",
         "{{json .Config.Labels}}\n{{json .State.Running}}", name],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    if result.returncode:
        if missing_ok and b"No such" in result.stderr:
            return None
        raise OpsError("No se pudo comprobar la identidad del contenedor de correo.")
    try:
        lines = result.stdout.decode().splitlines()
        labels, running = json.loads(lines[0]), json.loads(lines[1])
        expected_service = "mailserver" if name == CONTAINER else "roundcube"
        if name not in CONTAINERS or labels.get("com.docker.compose.project") != "naperu-mail":
            raise ValueError("project")
        if labels.get("com.docker.compose.service") != expected_service:
            raise ValueError("service")
        if name == CONTAINER:
            # Inspect only these public configuration fields, after validating ownership.
            template = ('{{range .Config.Env}}{{if or '
                        '(eq (index (split . "=") 0) "SSL_TYPE") '
                        '(eq (index (split . "=") 0) "SSL_CERT_PATH") '
                        '(eq (index (split . "=") 0) "SSL_KEY_PATH")'
                        '}}{{println .}}{{end}}{{end}}')
            environment = run(["docker", "inspect", "--format", template, name]).decode().splitlines()
            env = dict(item.split("=", 1) for item in environment if "=" in item)
            if (env.get("SSL_TYPE"), env.get("SSL_CERT_PATH"), env.get("SSL_KEY_PATH")) != (
                    "manual", CERT_PATH, KEY_PATH):
                raise ValueError("ssl")
        return bool(running)
    except (ValueError, IndexError, TypeError, AttributeError) as exc:

        raise OpsError("El contenedor no pertenece a la configuración de correo esperada.") from exc


def certificate_fingerprint(fullchain):
    der = run(["openssl", "x509", "-outform", "DER"], input=fullchain)
    return hashlib.sha256(der).hexdigest()


def copied_certificate_fingerprint(*, timeout=10):
    # Read only the public certificate DMS copies internally. Never read its private key.
    der = run(["docker", "exec", CONTAINER, "openssl", "x509",
               "-in", "/etc/dms/tls/cert", "-outform", "DER"], timeout=timeout)
    return hashlib.sha256(der).hexdigest()


def mail_tls_endpoint():
    if not container_state(CONTAINER, missing_ok=True):
        raise OpsError("El contenedor propio no está activo para verificar TLS.")
    output = run(["docker", "inspect", "--format",
                  '{{json (index .NetworkSettings.Ports "465/tcp")}}', CONTAINER])
    try:
        bindings = json.loads(output)
        endpoints = set()
        for item in bindings:
            address, port = item["HostIp"], int(item["HostPort"])
            if (address, port) == ("127.0.0.1", 2465):
                endpoints.add(("127.0.0.1", 2465))
            elif port == 465 and address in ("", "0.0.0.0", "127.0.0.1"):
                endpoints.add(("127.0.0.1", 465))
            elif (address, port) == (IP, 465):
                endpoints.add((IP, 465))
            else:
                raise ValueError("binding")
        if len(endpoints) != 1:
            raise ValueError("binding count")
        return endpoints.pop()
    except (TypeError, ValueError, KeyError) as exc:
        raise OpsError("El binding TLS no es el endpoint propio esperado de staging o publicación.") from exc


def loopback_tls_port():
    """Compatibility wrapper; new callers use mail_tls_endpoint for exact host+port."""
    return mail_tls_endpoint()[1]


def served_certificate_fingerprint(endpoint, *, cafile=None, timeout=10):
    if endpoint not in {("127.0.0.1", 2465), ("127.0.0.1", 465), (IP, 465)}:
        raise OpsError("Endpoint TLS fuera del ámbito propio.")
    # SNI and hostname validation remain exact even before public DNS is available.
    context = ssl.create_default_context(cafile=str(cafile) if cafile else None)
    with socket.create_connection(endpoint, timeout=timeout) as raw:
        with context.wrap_socket(raw, server_hostname=HOST) as secure:
            return hashlib.sha256(secure.getpeercert(binary_form=True)).hexdigest()


def reload_mail(expected_fingerprint, *, cafile=None, timeout=75, interval=2):
    if not container_state(CONTAINER, missing_ok=True):
        return False
    deadline = time.monotonic() + timeout
    # DMS manual SSL copies key then cert to /etc/dms/tls at its polling interval
    # (configured as 30s here). An immediate reload otherwise reads the old copy.
    while time.monotonic() < deadline:
        remaining = deadline - time.monotonic()
        try:
            if copied_certificate_fingerprint(timeout=max(0.1, min(10, remaining))) == expected_fingerprint:
                break
        except OpsError:
            pass  # Missing or partially copied public certificate is not ready yet.
        time.sleep(min(interval, max(0, deadline - time.monotonic())))
    else:
        raise OpsError("Timeout esperando la copia pública de DMS; certificado exportado conservado, recarga no confirmada.")
    if not container_state(CONTAINER, missing_ok=True):
        raise OpsError("DMS se detuvo durante la renovación; certificado exportado conservado.")
    run(["docker", "exec", CONTAINER, "postfix", "reload"], timeout=10)
    run(["docker", "exec", CONTAINER, "doveadm", "reload"], timeout=10)
    endpoint = mail_tls_endpoint()
    while time.monotonic() < deadline:
        remaining = deadline - time.monotonic()
        try:
            fingerprint = served_certificate_fingerprint(
                endpoint, cafile=cafile, timeout=max(0.1, min(10, remaining)))
            if fingerprint == expected_fingerprint:
                return True
        except (OSError, ssl.SSLError):
            pass  # TLS reload can become observable just after the command returns.
        time.sleep(min(interval, max(0, deadline - time.monotonic())))
    raise OpsError("Timeout verificando TLS servido con confianza normal; certificado exportado conservado, renovación no confirmada.")


def complete_certificate_export(root, expected, version, *, cafile=None):
    private_dir(root / ".local")
    statefile = root / ".local/cert-export-state.json"
    state = {"schema": 1, "fingerprint_sha256": expected, "version": version,
             "status": "pending", "serving_verified": False}
    atomic_private(statefile, json.dumps(state, sort_keys=True).encode() + b"\n")
    try:
        reloaded = reload_mail(expected, cafile=cafile)
    except OpsError:
        state["status"] = "failed"
        atomic_private(statefile, json.dumps(state, sort_keys=True).encode() + b"\n")
        raise
    state.update(status="passed" if reloaded else "pending", serving_verified=reloaded)
    atomic_private(statefile, json.dumps(state, sort_keys=True).encode() + b"\n")
    return reloaded


def incomplete_certificate_export(root, expected):
    statefile = root / ".local/cert-export-state.json"
    if not statefile.exists():
        return False
    try:
        state = json.loads(private_file(statefile).read_bytes())
        return (state["schema"] == 1 and state["fingerprint_sha256"] == expected
                and state["status"] != "passed")
    except (OSError, ValueError, KeyError, TypeError) as exc:
        raise OpsError("Estado de renovación privado inválido; requiere revisión.") from exc


def export_cert(root=ROOT, acme=ACME, *, cafile=None, minimum_seconds=604800,
                inspect=True):
    # Read only the exact resolver and exact primary domain, never a wildcard or SAN neighbour.
    try:
        data = json.loads(acme.read_bytes())
        entries = data["letsencrypt"]["Certificates"]
        matches = [entry for entry in entries if entry.get("domain", {}).get("main") == HOST]
        if len(matches) != 1:
            raise ValueError("selection")
        cert = base64.b64decode(matches[0]["certificate"], validate=True)
        key = base64.b64decode(matches[0]["key"], validate=True)
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as exc:
        raise OpsError("ACME no contiene una lectura completa y única del certificado requerido; se conserva el vigente.") from exc
    certdir = root / "certs"
    private_dir(certdir)
    private_dir(certdir / "versions")
    for filename in ("fullchain.pem", "privkey.pem"):
        path = certdir / filename
        if os.path.lexists(path):
            if not path.is_symlink() or os.readlink(path) != "current/" + filename:
                raise OpsError("El certificado requiere el layout atómico certs/current.")
    if os.path.lexists(certdir / "current"):
        current = certdir / "current"
        if not current.is_symlink() or not re.fullmatch(r"versions/[A-Za-z0-9_-]+", os.readlink(current)):
            raise OpsError("Puntero de certificado vigente inválido.")
    stage = Path(tempfile.mkdtemp(prefix="candidate_", dir=certdir / "versions"))
    stage.chmod(0o700)
    switched = False
    try:
        validate_cert(cert, key, stage, cafile=cafile, minimum_seconds=minimum_seconds)
        existing_cert, existing_key = certdir / "fullchain.pem", certdir / "privkey.pem"
        unchanged = (existing_cert.exists() and existing_key.exists()
                     and existing_cert.read_bytes() == (stage / "fullchain.pem").read_bytes()
                     and existing_key.read_bytes() == (stage / "privkey.pem").read_bytes())
        expected = certificate_fingerprint((stage / "fullchain.pem").read_bytes())
        if unchanged:
            # Retry a previously incomplete rollout without creating another cert version.
            if inspect and incomplete_certificate_export(root, expected):

                reloaded = complete_certificate_export(
                    root, expected, os.readlink(certdir / "current"), cafile=cafile)
                return {"changed": False, "reloaded": reloaded, "serving_verified": reloaded}
            return {"changed": False, "reloaded": False}
        if inspect:
            container_state(CONTAINER, missing_ok=True)  # Guard before committing the new pair.
        for filename in ("fullchain.pem", "privkey.pem"):
            target = certdir / filename
            if not os.path.lexists(target):
                target.symlink_to("current/" + filename)
        link = certdir / (".current-" + secrets.token_hex(8))
        link.symlink_to("versions/" + stage.name)
        try:
            os.replace(link, certdir / "current")
            fsync_dir(certdir)
            switched = True
        finally:
            if os.path.lexists(link):
                link.unlink()
        reloaded = complete_certificate_export(
            root, expected, "versions/" + stage.name, cafile=cafile) if inspect else False
        return {"changed": True, "reloaded": reloaded, "serving_verified": reloaded}
    finally:
        if not switched:
            shutil.rmtree(stage)


def init_key(root=ROOT):
    private_dir(root / ".local")
    target = root / ".local/backup.key"
    fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "wb") as handle:
        handle.write(base64.b64encode(os.urandom(64)) + b"\n")
        handle.flush()
        os.fsync(handle.fileno())
    fsync_dir(target.parent)
    return {"created": True, "path": str(target), "offline_copy_required": True}


def sha_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def inventory(root):
    entries = []
    omissions = []
    def visit(path, name):
        info = path.lstat()
        item = {"name": name, "mode": stat.S_IMODE(info.st_mode), "uid": info.st_uid, "gid": info.st_gid}
        if stat.S_ISDIR(info.st_mode):
            item["type"] = "dir"
            entries.append(item)
            for child in sorted(path.iterdir(), key=lambda p: p.name):
                visit(child, name + "/" + child.name)
        elif stat.S_ISREG(info.st_mode):
            item.update(type="file", bytes=info.st_size, sha256=sha_file(path))
            entries.append(item)
        elif stat.S_ISLNK(info.st_mode):
            target = os.readlink(path)
            if os.path.isabs(target) or not path.resolve().is_relative_to(root.resolve()):
                raise OpsError("El snapshot contiene un enlace fuera de su ámbito.")
            item.update(type="symlink", target=target)
            entries.append(item)
        else:
            kind = "socket" if stat.S_ISSOCK(info.st_mode) else "fifo" if stat.S_ISFIFO(info.st_mode) else "device-or-unknown"
            if RUNTIME_OMISSIONS.get(name) != kind:
                raise OpsError("El snapshot contiene un archivo especial fuera de la allowlist de ejecución.")
            omissions.append({"name": name, "type": kind, "reason": "runtime-only-allowlist"})
    for component in COMPONENTS:
        path = root / component
        if not path.exists() or path.is_symlink():
            raise OpsError("Falta un componente obligatorio del snapshot.")
        visit(path, component)
    return {"schema": 2, "components": list(COMPONENTS), "entries": entries, "omissions": omissions}


@contextlib.contextmanager
def maintenance(*, offline=False):
    states = {name: container_state(name, missing_ok=offline) for name in CONTAINERS}
    if offline and any(states.values()):
        raise OpsError("El respaldo offline exige que ambos servicios propios estén detenidos.")
    stopped = []
    try:
        for name in reversed(CONTAINERS):  # Close webmail first; preserve mail queue on disk.
            if states[name]:
                stopped.append(name)
                run(["docker", "stop", "--time", "30", name], timeout=60)
        yield
    finally:
        errors = []
        for name in CONTAINERS:
            if name in stopped:
                try:
                    run(["docker", "start", name], timeout=60)
                except OpsError:
                    errors.append(name)
        if errors:
            raise OpsError("No se pudo reanudar un servicio propio; requiere intervención.")


def gpg_base(home, key):
    private_dir(home)
    private_file(key)
    return ["gpg", "--no-options", "--homedir", str(home), "--batch", "--yes",
            "--no-symkey-cache", "--pinentry-mode", "loopback",
            "--passphrase-file", str(key)]


def summarize(manifest):
    result = {}
    for component in COMPONENTS:
        items = [entry for entry in manifest["entries"]
                 if entry["name"] == component or entry["name"].startswith(component + "/")]
        result[component] = {"files": sum(e["type"] == "file" for e in items),
                             "bytes": sum(e.get("bytes", 0) for e in items),
                             "runtime_omitted": sum(
                                 entry["name"] == component or entry["name"].startswith(component + "/")
                                 for entry in manifest.get("omissions", []))}
    return result


def backup(root=ROOT, *, offline=False, key=None):
    key = Path(key) if key else root / ".local/backup.key"
    private_file(key)
    backupdir = root / ".local/backups"
    private_dir(backupdir)
    identifier = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + secrets.token_hex(6)
    final = backupdir / (identifier + ".tar.gpg")
    pending = backupdir / ("." + identifier + ".pending")
    fd = os.open(pending, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    os.close(fd)
    try:
        with tempfile.TemporaryDirectory(prefix="mail-backup-", dir=root / ".local") as scratch:
            home = Path(scratch) / "gpg"
            args = gpg_base(home, key) + ["--cipher-algo", "AES256", "--force-mdc",
                    "--compress-algo", "none", "--symmetric", "--output", str(pending)]
            with maintenance(offline=offline):
                manifest = inventory(root)
                process = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                                           stderr=subprocess.PIPE)
                try:
                    with tarfile.open(fileobj=process.stdin, mode="w|", dereference=False) as archive:
                        content = json.dumps(manifest, sort_keys=True).encode()
                        member = tarfile.TarInfo("MANIFEST.json")
                        member.size, member.mode = len(content), 0o600
                        archive.addfile(member, io.BytesIO(content))
                        # Add precisely the inventoried data; tar's implicit traversal would
                        # otherwise include FIFOs despite their explicit runtime omission.
                        for entry in manifest["entries"]:
                            archive.add(root / entry["name"], arcname=entry["name"], recursive=False)
                    process.stdin.close()
                    process.stdin = None
                    _, stderr = process.communicate(timeout=300)
                    if process.returncode:
                        raise OpsError("El cifrado del respaldo falló.")
                    if inventory(root) != manifest:
                        raise OpsError("El contenido cambió durante el snapshot.")
                except BaseException:
                    if process.poll() is None:
                        process.kill()
                    process.wait()
                    raise
            # Services resume before the full decrypt-and-check pass.
            checked = verify_backup(pending, key)
            if checked["components"] != summarize(manifest):

                raise OpsError("La verificación no corresponde al snapshot.")
            with open(pending, "rb") as handle:
                os.fsync(handle.fileno())
            os.link(pending, final)  # Atomic creation; an existing backup is never overwritten.
            pending.unlink()
            sidecar = {"schema": 2, "created_utc": identifier.split("-")[0],
                       "encryption": "AES256+MDC", "encrypted_sha256": sha_file(final),
                       "components": checked["components"], "verified": True}
            atomic_private(backupdir / (identifier + ".json"), json.dumps(sidecar, indent=2).encode() + b"\n")
            fsync_dir(backupdir)
            return {"path": str(final), "verified": True, "components": checked["components"]}
    finally:
        if pending.exists():
            pending.unlink()


@contextlib.contextmanager
def decrypted_archive(backup_file, key):
    private_file(backup_file)
    private_file(key)
    with tempfile.TemporaryDirectory(prefix="mail-verify-") as scratch:
        with tempfile.TemporaryFile(mode="w+b") as plaintext:
            args = gpg_base(Path(scratch) / "gpg", key) + ["--status-fd", "2", "--decrypt", str(backup_file)]
            result = subprocess.run(args, stdout=plaintext, stderr=subprocess.PIPE, check=False)
            statuses = result.stderr.splitlines()
            if (result.returncode or b"[GNUPG:] GOODMDC" not in statuses
                    or b"[GNUPG:] DECRYPTION_OKAY" not in statuses):
                raise OpsError("Respaldo no autenticado, dañado o clave incorrecta.")
            plaintext.seek(0)
            try:
                with tarfile.open(fileobj=plaintext, mode="r:") as archive:
                    yield archive
            except (tarfile.TarError, OSError) as exc:
                raise OpsError("El respaldo descifrado no es un archivo válido.") from exc


def checked_archive(archive):
    members = archive.getmembers()
    byname = {}
    for member in members:
        name = member.name.rstrip("/")
        path = PurePosixPath(name)
        if (not name or path.is_absolute() or ".." in path.parts or "." in path.parts
                or str(path) != name or name in byname or member.islnk()
                or not (member.isdir() or member.isfile() or member.issym())):
            raise OpsError("El respaldo contiene una ruta o tipo peligroso.")
        byname[name] = member
    if "MANIFEST.json" not in byname or not byname["MANIFEST.json"].isfile():
        raise OpsError("Falta el manifiesto del respaldo.")
    if byname["MANIFEST.json"].size > 32 * 1024 * 1024:
        raise OpsError("Manifiesto del respaldo excesivo.")
    try:
        manifest = json.load(archive.extractfile(byname["MANIFEST.json"]))
        if manifest["schema"] != 2 or manifest["components"] != list(COMPONENTS):
            raise ValueError("schema")
        entries = manifest["entries"]
        omissions = manifest.setdefault("omissions", [])  # Older schema2 snapshots omitted none.
        if not isinstance(omissions, list):
            raise ValueError("omissions type")
        omitted_names = set()
        for omitted in omissions:
            name, kind = omitted["name"], omitted["type"]
            if (name in omitted_names or name in byname or RUNTIME_OMISSIONS.get(name) != kind
                    or omitted["reason"] != "runtime-only-allowlist"):
                raise ValueError("omission allowlist")
            omitted_names.add(name)
        if any(component not in byname for component in COMPONENTS):
            raise ValueError("missing component")
        if len(entries) != len(byname) - 1:
            raise ValueError("count")
        names = {entry["name"] for entry in entries}
        if len(names) != len(entries) or names != set(byname) - {"MANIFEST.json"}:
            raise ValueError("names")
        for entry in entries:
            name = entry["name"]
            if not any(name == c or name.startswith(c + "/") for c in COMPONENTS):
                raise ValueError("scope")
            member = byname[name]
            if stat.S_IMODE(member.mode) != entry["mode"]:
                raise ValueError("mode")
            if (not all(isinstance(entry[field], int) and 0 <= entry[field] < 2**32 - 1
                        for field in ("uid", "gid"))
                    or (member.uid, member.gid) != (entry["uid"], entry["gid"])):
                raise ValueError("ownership")
            if any(byname.get(str(parent)) and byname[str(parent)].issym()
                   for parent in PurePosixPath(name).parents):
                raise ValueError("symlink parent")
            kind = entry["type"]
            if kind == "file" and member.isfile():
                if member.size != entry["bytes"]:
                    raise ValueError("size")
                digest = hashlib.sha256()
                stream = archive.extractfile(member)
                for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(chunk)
                if digest.hexdigest() != entry["sha256"]:
                    raise ValueError("hash")
            elif kind == "dir" and member.isdir():
                pass
            elif kind == "symlink" and member.issym():
                if member.linkname != entry["target"] or posixpath.isabs(member.linkname):
                    raise ValueError("target")
                target = posixpath.normpath(posixpath.join(posixpath.dirname(name), member.linkname))
                if target.startswith("../"):
                    raise ValueError("scope target")
                seen = {name}
                # Resolve every symlink path segment; refuse escape or loops.
                for _ in range(len(byname) + 1):
                    parts = PurePosixPath(target).parts
                    found = False
                    for index in range(1, len(parts) + 1):
                        prefix = "/".join(parts[:index])
                        if prefix in byname and byname[prefix].issym():
                            if prefix in seen:
                                raise ValueError("loop")
                            seen.add(prefix)
                            nested = byname[prefix].linkname
                            if posixpath.isabs(nested):
                                raise ValueError("absolute")
                            target = posixpath.normpath(posixpath.join(
                                posixpath.dirname(prefix), nested, *parts[index:]))
                            if target.startswith("../") or target not in byname:
                                raise ValueError("outside")
                            found = True
                            break
                    if not found:
                        if target not in byname:
                            raise ValueError("missing target")
                        break
                else:
                    raise ValueError("loop")
            else:
                raise ValueError("type")
        return manifest, byname
    except (ValueError, KeyError, TypeError, AttributeError) as exc:
        raise OpsError("El contenido no coincide con el manifiesto íntegro del respaldo.") from exc


def verify_backup(backup_file, key):
    with decrypted_archive(Path(backup_file), Path(key)) as archive:
        manifest, _ = checked_archive(archive)
        return {"verified": True, "components": summarize(manifest)}


def restore_test(backup_file, key, destination, root=ROOT):
    destination = Path(destination)
    restorebase = root / ".local/restore-tests"
    private_dir(restorebase)
    if (not destination.is_absolute() or destination.parent.resolve() != restorebase.resolve()
            or destination.name in ("", ".", "..") or os.path.lexists(destination)):
        raise OpsError("La restauración exige un directorio nuevo dentro de .local/restore-tests.")
    with decrypted_archive(Path(backup_file), Path(key)) as archive:

        manifest, byname = checked_archive(archive)  # Check every path/hash/owner before writing.
        if os.geteuid() != 0:
            groups = set(os.getgroups()) | {os.getegid()}
            if any(entry["uid"] != os.geteuid() or entry["gid"] not in groups
                   for entry in manifest["entries"]):
                raise OpsError("No hay permiso para reproducir UID/GID del snapshot.")
        destination.mkdir(mode=0o700)
        try:
            for name, member in byname.items():
                if name == "MANIFEST.json" or member.issym():
                    continue
                target = destination / name
                target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
                if member.isdir():
                    target.mkdir(exist_ok=True, mode=0o700)
                else:
                    with target.open("xb") as handle:
                        shutil.copyfileobj(archive.extractfile(member), handle)
                    os.chown(target, member.uid, member.gid)
                    target.chmod(member.mode & 0o7777)
            for name, member in byname.items():
                if member.issym():
                    (destination / name).symlink_to(member.linkname)
                    os.chown(destination / name, member.uid, member.gid, follow_symlinks=False)
            # Match source modes after creating all children.
            for name, member in reversed(list(byname.items())):
                if member.isdir():
                    os.chown(destination / name, member.uid, member.gid)
                    (destination / name).chmod(member.mode & 0o7777)
            restored_manifest = inventory(destination)
            if restored_manifest["omissions"]:
                raise OpsError("El destino aislado contiene artefactos de ejecución inesperados.")
            # Omitted IPC endpoints are recreated by services on startup, never by restore.
            restored_manifest["omissions"] = manifest["omissions"]
            if restored_manifest != manifest:
                raise OpsError("La restauración no reproduce íntegramente datos, UID/GID y permisos del snapshot.")
            atomic_private(destination / "RESTORE-VERIFIED.json",
                           json.dumps({"verified": True, "components": summarize(manifest)}, indent=2).encode())
        except BaseException:
            shutil.rmtree(destination)
            raise
    return {"verified": True, "destination": str(destination), "components": summarize(manifest)}


def dns_response(resolver, host, record, *, authoritative=False):
    """Read DNS metadata without accepting CNAMEs, wrong owners or error statuses."""
    args = ["dig", "@" + resolver, "+time=4", "+tries=1", "+noall", "+comments", "+answer"]
    if authoritative:
        args.append("+norecurse")
    args += ["-x", host] if record == "PTR" else [host, record]
    output = run(args, timeout=15).decode("ascii", errors="replace")
    if not re.search(r"status: NOERROR[,\s]", output):
        raise OpsError("El resolver DNS no confirmó una respuesta NOERROR.")
    flags = re.search(r";;\s+flags:\s*([^;]*);", output)
    is_authoritative = bool(flags and "aa" in flags.group(1).split())
    if authoritative and not is_authoritative:
        raise OpsError("El servidor de reverse DNS no confirmó una respuesta autoritativa AA.")
    owner = ".".join(reversed(host.split("."))) + ".in-addr.arpa" if record == "PTR" else host.rstrip(".").lower()
    answers, ttls = [], []
    for line in output.splitlines():
        if line and not line.startswith(";"):
            fields = line.split()
            try:
                if (len(fields) != 5 or fields[0].rstrip(".").lower() != owner
                        or fields[2] != "IN" or fields[3] != record):
                    raise ValueError("record")
                ttl = int(fields[1])
                if ttl < 0:
                    raise ValueError("ttl")
            except (ValueError, IndexError) as exc:
                raise OpsError("La respuesta DNS contiene un registro, propietario o TTL inesperado.") from exc
            answers.append(fields[4].rstrip(".").lower())
            ttls.append(ttl)
    return {"values": answers, "ttl_seconds": ttls, "authoritative": is_authoritative}


def dns_query(resolver, host, record):
    return dns_response(resolver, host, record)["values"]


def check_ptr_public():
    """Confirm published PTR; permit only positive-TTL legacy cache with exact authorities."""
    recursive = []
    for resolver in RESOLVERS:
        response = dns_response(resolver, IP, "PTR")
        if (len(response["values"]) != 1 or len(response["ttl_seconds"]) != 1
                or response["values"][0] not in (HOST, PTR_LEGACY)
                or response["ttl_seconds"][0] <= 0):
            raise OpsError("PTR público inesperado, múltiple o sin TTL positivo.")
        recursive.append({"resolver": resolver, "value": response["values"][0],
                          "ttl_seconds": response["ttl_seconds"][0]})
    report = {"verified": True, "propagation_pending": False, "source": "recursive",
              "recursive": recursive, "authorities": [], "dns_queries": 2}
    if all(item["value"] == HOST for item in recursive):
        return report
    # This narrow exception covers only the known old PTR still cached with TTL>0.
    # Confirm delegation using both public recursors, then query both named authorities.
    for resolver in RESOLVERS:
        delegation = dns_response(resolver, PTR_ZONE, "NS")
        if (len(delegation["values"]) != 2 or set(delegation["values"]) != set(PTR_AUTHORITIES)
                or len(delegation["ttl_seconds"]) != 2
                or any(ttl <= 0 for ttl in delegation["ttl_seconds"])):
            raise OpsError("La delegación reverse DNS no coincide con ambas autoridades Hostinger esperadas.")
    authorities = []
    for server in PTR_AUTHORITIES:
        response = dns_response(server, IP, "PTR", authoritative=True)
        if (not response["authoritative"] or response["values"] != [HOST]
                or len(response["ttl_seconds"]) != 1 or response["ttl_seconds"][0] <= 0):
            raise OpsError("Las autoridades reverse DNS no confirman PTR exacto, AA y TTL positivo concordantes.")
        authorities.append({"server": server, "value": HOST,
                            "ttl_seconds": response["ttl_seconds"][0], "authoritative": True})
    report.update(propagation_pending=True, source="authoritative", authorities=authorities,
                  delegation={"zone": PTR_ZONE, "names": list(PTR_AUTHORITIES)}, dns_queries=6)
    return report


def tls_socket(host, port):
    context = ssl.create_default_context()
    with socket.create_connection((IP, port), timeout=12) as raw:
        with context.wrap_socket(raw, server_hostname=host) as secure:
            secure.getpeercert()  # Normal system trust and hostname verification are mandatory.


def check_public(*, mail_tls=False):
    checks = []
    for resolver in RESOLVERS:
        for host in (HOST, WEBMAIL):
            if dns_query(resolver, host, "A") != [IP] or dns_query(resolver, host, "AAAA"):
                raise OpsError("DNS público pendiente: A exacto y ausencia de AAAA requeridos en ambos resolvers.")
            checks.append(resolver + "/" + host)
    ptr_report = check_ptr_public()
    try:
        for host in (HOST, WEBMAIL):
            tls_socket(host, 443)
        if mail_tls:
            for port in (465, 993):
                tls_socket(HOST, port)
            with smtplib.SMTP(HOST, 587, timeout=12) as client:
                client.ehlo()
                client.starttls(context=ssl.create_default_context())
                client.ehlo()
    except (OSError, ssl.SSLError, smtplib.SMTPException) as exc:
        raise OpsError("La comprobación TLS pública con confianza normal no pasó.") from exc
    return {"verified": True, "dns_checks": len(checks) * 2 + ptr_report["dns_queries"],

            "ptr": ptr_report, "propagation_pending": ptr_report["propagation_pending"],
            "tls": ["mail:443", "webmail:443"] + (["mail:465", "mail:993", "mail:587"] if mail_tls else [])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("export-cert")
    commands.add_parser("init-backup-key")
    backup_command = commands.add_parser("backup")
    backup_command.add_argument("--offline", action="store_true",
                                help="Exige ambos contenedores propios detenidos o ausentes.")
    for name in ("verify-backup", "restore-test"):
        command = commands.add_parser(name)
        command.add_argument("backup_file", type=Path)
        command.add_argument("--key-file", type=Path, default=ROOT / ".local/backup.key")
        if name == "restore-test":
            command.add_argument("--destination", type=Path, required=True)
    public_command = commands.add_parser("check-public")
    public_command.add_argument("--mail-tls", action="store_true")
    args = parser.parse_args()
    try:
        if os.geteuid() != 0:
            raise OpsError("Esta utilidad administrativa exige root.")
        if args.command == "check-public":
            result = check_public(mail_tls=args.mail_tls)
        else:
            with locked(ROOT):
                if args.command == "export-cert":
                    result = export_cert()
                elif args.command == "init-backup-key":
                    result = init_key()
                elif args.command == "backup":
                    result = backup(offline=args.offline)
                elif args.command == "verify-backup":
                    result = verify_backup(args.backup_file, args.key_file)
                else:
                    result = restore_test(args.backup_file, args.key_file, args.destination)
        print(json.dumps(result, sort_keys=True))
        return 0
    except (OpsError, OSError) as exc:
        message = str(exc) if isinstance(exc, OpsError) else "Error de archivo o permiso; no se completó la operación."
        print("mail-ops: " + message, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
