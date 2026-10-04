#!/usr/bin/env python3
"""Read-only SMTP network contract for Acrópolis; does not operate the mail service."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys

APP_ROOT = Path(__file__).resolve().parents[1]
PROVIDER_ROOT = Path('/root/proyect/naperu-mail')
HOST = 'mail.naperu.cloud'
SMTP_NETWORK = 'naperu-mail-smtp'
SMTP_NETWORK_LABELS = {'com.naperu.mail.owner': 'naperu-mail',
                       'com.naperu.mail.role': 'application-smtp'}
NETWORK_KEYS = ('Name', 'Id', 'Driver', 'Scope', 'Internal', 'EnableIPv6', 'Options', 'Labels', 'Containers')
NETWORK_TEMPLATE = '\n'.join('{{json .' + key + '}}' for key in NETWORK_KEYS)
MEMBER_TEMPLATE = '{{json .Config.Labels}}\n{{json .NetworkSettings.Networks}}'
MAX_INSPECT_BYTES = 1024 * 1024


class NetworkError(RuntimeError):
    pass


def inspect_output(arguments, *, timeout=15):
    network_query = ['docker', 'network', 'inspect', '--format', NETWORK_TEMPLATE, SMTP_NETWORK]
    member_query = (len(arguments) == 5 and arguments[:4] == ['docker', 'inspect', '--format', MEMBER_TEMPLATE]
                    and re.fullmatch(r'[a-f0-9]{64}', arguments[4]))
    if arguments != network_query and not member_query:
        raise NetworkError('Sólo se permite inspeccionar la red SMTP propia y sus miembros exactos.')
    try:
        result = subprocess.run(arguments, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise NetworkError('No se pudo inspeccionar la integración SMTP privada; salida omitida.') from None
    if result.returncode or len(result.stdout) > MAX_INSPECT_BYTES:
        raise NetworkError('Inspección SMTP rechazada o fuera de límites; salida privada omitida.')
    return result.stdout


def read_smtp_network():
    output = inspect_output(['docker', 'network', 'inspect', '--format', NETWORK_TEMPLATE, SMTP_NETWORK])
    try:
        values = [json.loads(line) for line in output.decode().splitlines()]
        if len(values) != len(NETWORK_KEYS):
            raise ValueError('shape')
        return dict(zip(NETWORK_KEYS, values))
    except (ValueError, TypeError, UnicodeError):
        raise NetworkError('Metadatos de la red SMTP propia inválidos.') from None


def validate_network_shape(network):
    if (not isinstance(network, dict) or network.get("Name") != SMTP_NETWORK
            or network.get("Driver") != "bridge" or network.get("Scope") != "local"
            or network.get("Internal") is not True or network.get("EnableIPv6") is not False
            or network.get("Options") not in (None, {})
            or network.get("Labels") != SMTP_NETWORK_LABELS
            or not re.fullmatch(r"[a-f0-9]{64}", str(network.get("Id", "")))):
        raise NetworkError("La red SMTP debe ser el bridge interno propio con etiquetas exactas.")
    if not isinstance(network.get("Containers"), dict):
        raise NetworkError("Miembros de la red SMTP no disponibles.")


def validate_smtp_network(network, members, *, require_mail=False):
    """Validate an explicitly owned internal bridge and every attached workload."""
    validate_network_shape(network)
    attached = network.get("Containers")
    if not isinstance(attached, dict) or not isinstance(members, dict) or set(attached) != set(members):
        raise NetworkError("No se pudo demostrar cada miembro de la red SMTP propia.")
    mail_count = 0
    for identifier, member in members.items():
        labels = member.get("labels", {}) if isinstance(member, dict) else {}
        if not isinstance(labels, dict):
            raise NetworkError("Etiquetas de miembro SMTP inválidas.")
        project = labels.get("com.docker.compose.project")
        service = labels.get("com.docker.compose.service")
        allowed_mail = project == "naperu-mail" and service == "mailserver"
        allowed_app = project == "acropolis-channel" and service in ("web", "migrations")
        directory = str(PROVIDER_ROOT if allowed_mail else APP_ROOT)
        if (not (allowed_mail or allowed_app)
                or labels.get("com.docker.compose.project.working_dir") != directory):
            raise NetworkError("La red SMTP contiene un servicio fuera de los proyectos y roles autorizados.")
        networks = member.get("networks", {})
        endpoint = networks.get(SMTP_NETWORK, {}) if isinstance(networks, dict) else {}
        if not isinstance(endpoint, dict):
            raise NetworkError("Endpoint de miembro SMTP inválido.")
        aliases = endpoint.get("Aliases") or []
        if (endpoint.get("NetworkID") != network["Id"] or not isinstance(aliases, list)
                or any(not isinstance(alias, str) for alias in aliases)):
            raise NetworkError("El miembro SMTP no corresponde a la red propia comprobada.")
        normalized_aliases = [alias.casefold().rstrip(".") for alias in aliases]
        if allowed_mail:
            mail_count += 1
            if (set(networks) != {"naperu-mail-private", SMTP_NETWORK}
                    or aliases.count(HOST) != 1 or normalized_aliases.count(HOST) != 1):
                raise NetworkError("DMS requiere sólo sus dos redes privadas y el alias TLS exacto.")
        elif HOST in normalized_aliases:
            raise NetworkError("El alias SMTP exacto sólo puede pertenecer al mailserver propio.")
    if mail_count > 1 or (require_mail and mail_count != 1):
        raise NetworkError("La red SMTP requiere exactamente un mailserver propio activo.")
    return {"name": SMTP_NETWORK, "internal": True, "owner_verified": True,
            "members_verified": len(members), "mailserver_attached": mail_count == 1}


def check_smtp_network(*, require_mail=False):
    network = read_smtp_network()
    validate_network_shape(network)
    attached = network.get("Containers")
    if not isinstance(attached, dict):
        raise NetworkError("Miembros de la red SMTP no disponibles.")
    members = {}
    for identifier in attached:
        if not re.fullmatch(r"[a-f0-9]{64}", identifier):
            raise NetworkError("Identidad de miembro SMTP inválida.")
        output = inspect_output(["docker", "inspect", "--format",
                      "{{json .Config.Labels}}\n{{json .NetworkSettings.Networks}}", identifier], timeout=15)
        try:
            labels, networks = [json.loads(line) for line in output.decode().splitlines()]
            members[identifier] = {"labels": labels, "networks": networks}
        except (ValueError, TypeError) as exc:
            raise NetworkError("No se pudo verificar el miembro SMTP propio.") from exc
    proof = validate_smtp_network(network, members, require_mail=require_mail)
    if read_smtp_network() != network:
        raise NetworkError("La red SMTP cambió durante la verificación; reintentar sin modificarla.")
    return proof


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--allow-empty', action='store_true',
                        help='Permite ausencia de DMS para correo deshabilitado o externo; valida cada miembro existente.')
    args = parser.parse_args()
    try:
        print(json.dumps(check_smtp_network(require_mail=not args.allow_empty), sort_keys=True))
        return 0
    except Exception as error:
        reason = str(error) if isinstance(error, NetworkError) else 'No se pudo comprobar la integración SMTP; salida privada omitida.'
        print(json.dumps({'status': 'blocked', 'reason': reason}, sort_keys=True), file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
