# Integración SMTP de Acrópolis Channel

Acrópolis consume un servicio de correo independiente. Su fuente, recetas, webmail, certificados y mantenimiento pertenecen a `/root/proyect/naperu-mail/source`, Git local independiente; su runtime y datos privados están en `/root/proyect/naperu-mail`. No desarrollar ni copiar ese servidor dentro del repositorio de Acrópolis. Para operarlo, leer su AGENTS y su skill naperu-mail-operations; sus timers utilizan una copia certificada en `.local/operations/current`, separada del HEAD de desarrollo.

## Contrato del cliente

SMTP propio: `mail.naperu.cloud`, puerto `465`, seguridad `ssl` (TLS implícito), autenticación con `notificaciones@naperu.cloud` y remitente del mismo correo, nombre visible Acrópolis Channel. Las credenciales permanecen exclusivamente en el VPS y la configuración privada de la aplicación. No pedirlas por chat ni versionarlas.

Web y runner de migraciones usan la red externa para Compose `naperu-mail-smtp`, bridge interna con etiquetas exactas `com.naperu.mail.owner=naperu-mail` y `com.naperu.mail.role=application-smtp`. External significa red precreada, no acceso a Internet. El alias TLS `mail.naperu.cloud` pertenece sólo al mailserver propio. No sustituirlo por la IP pública, IP de contenedor o nombre sin certificado; conservar confianza TLS normal y comprobación de hostname.

La red admite únicamente DMS de naperu-mail y web/migrations de acropolis-channel, con sus directorios Compose canónicos. DMS mantiene además naperu-mail-private para su salida; la base de Acrópolis, Roundcube, Traefik y otros proyectos no entran a esta red SMTP. El proyecto de correo prepara y mantiene la red; Acrópolis sólo inspecciona el contrato.

`scripts/smtp-network.py` es un guard de lectura del cliente: sólo ejecuta Docker inspect sobre esa red y sus miembros, sin gestionar certificados, buzones, credenciales, backups o redes. Rechaza propiedad/miembros/alias incorrectos y cambios durante la inspección. Su CLI exige DMS por defecto; `--allow-empty` permite ausencia de DMS cuando el correo está deshabilitado o usa un proveedor externo, conservando todas las demás restricciones. El preflight de deploy siempre comprueba esa red porque Compose adjunta web y runner también con correo deshabilitado. El guard de identidad exige DMS para SMTP propio habilitado.

## Activación y pruebas

Mantener `IDENTITY_EMAIL_ENABLED=false` hasta comprobar DNS/PTR/TLS y entrega real controlada del servicio independiente. Ese modo bloquea registro, reenvío y solicitud de recuperación; conserva acceso de cuentas confirmadas. PFX, key ring, HTTPS y proxies exactos son obligatorios en ambos modos.

Para una activación autorizada, guardar el `.env` previo en privado y preparar las claves `IDENTITY_SMTP_*` conforme al contrato. El proceso del VPS lee la contraseña del servicio sin imprimirla. Verificar `python3 scripts/identity-runtime.py --check`. Con candidato limpio y QA propia aprobada, publicar el SHA y ejecutar `bash scripts/deploy.sh --expected-sha SHA`; su runner certificado comprueba TLS y autenticación SMTP antes de respaldar/migrar/cambiar la aplicación. Conservar recuperación compatible y el estado real en `.local/`.

El gate de Acrópolis verifica su guard con metadata sintética y pruebas de identidad/despliegue; mantiene SMTP/CA propios para outbox, TLS, autenticación, reintentos, registro, confirmación y recuperación. No importa pruebas del servidor de correo ni usa sus credenciales productivas. La QA del correo se ejecuta en su repositorio y no certifica las imágenes de Acrópolis.

Después de publicar, comprobar capabilities, salud y entrega real. El propietario completa el registro, confirmación explícita, acceso y recuperación con su correo designado. No crear cuentas ficticias en producción, confirmar por atajo, solicitar contraseñas/enlaces privados ni conceder permisos administrativos sin designación. Las pruebas reales autorizadas en este trabajo se limitan al correo externo designado por el propietario y registrado en la continuidad privada.

## Portal y operación independiente

La bandeja se revisa en https://webmail.naperu.cloud mediante Roundcube, con el usuario soporte@naperu.cloud. Ese portal usa HTTPS443; SMTP25 recibe correo público, mientras SMTP465 e IMAP993 permanecen privados y 587 no se publica. No desarrollar un webmail propio dentro de Acrópolis.

El servicio tiene un límite inicial configurado de cuatro mensajes por minuto; no acredita capacidad de campañas ni entrega inmediata bajo colas grandes. Aceptación SMTP no equivale a llegada a Entrada. La entrega externa, los rebotes y la reputación se observan en el servicio independiente. Su backup cifrado de buzones/configuración y el bundle de su código son distintos del backup de PostgreSQL/key ring de Acrópolis.
