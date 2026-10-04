# Correo propio de Naperu y SMTP de Acrópolis Channel

El propietario autorizó el 4 de octubre de 2026 alojar correo en el VPS para evitar contratar buzones. El dominio es naperu.cloud. El servicio independiente vive en /root/proyect/naperu-mail; sus recetas versionadas pertenecen a infra/mail. No almacenar código en Windows. La preparación no acredita publicación ni entrega externa.

## Contrato

SMTP: mail.naperu.cloud, puerto 465, TLS implícito y autenticación. Usuario/remitente notificaciones@naperu.cloud, nombre Acrópolis Channel. Soporte: soporte@naperu.cloud; aliases postmaster y abuse. Webmail: https://webmail.naperu.cloud; IMAP TLS 993, submission TLS 465 o STARTTLS 587. Registro y recuperación de contraseña sólo se habilitan tras DNS/PTR/TLS y entrega controlada. No crear administrador por atajo.

Las contraseñas generadas están exclusivamente en /root/proyect/naperu-mail/.local/credentials.json, privado 600. No imprimirlo en herramientas/chat ni incluirlo en Git. La clave Roundcube, cuentas, DKIM privado, buzones, cola y backups están fuera del repositorio. La clave pública DKIM se puede publicar.

## Preparación y límites

Imágenes Docker Mailserver 16.0.1 y Roundcube 1.7.4 fijadas por digest en images.lock.json/runtime.env. Compose naperu-mail; memoria 3 GB mail / 384 MB webmail, CPU 1.5/0.5. Configurar mediante scripts/provision.py sin regenerar credenciales existentes. TLS de STAGE es privado, limitado a loopback; nunca habilitarlo públicamente ni añadir su CA a Acrópolis.

STAGE usa compose.yml + stage.yml: SMTP 2525/2465/2587, IMAP 2993 y HTTP 20888, todos loopback. Publicar usa exclusivamente compose.yml + public.yml, sin acumular stage.yml. Mailserver no entra a dokploy-network; Roundcube usa esa red sólo para HTTP y la red independiente de correo para SMTP/IMAP autenticados. PERMIT_DOCKER=none y mynetworks127/8; nunca confiar en subredes para relay. El modo público exige cookies Secure/SameSite y Fail2ban en el namespace propio.

Toda salida remota comparte transport smtp: default_transport=smtp, relay_transport=smtp, smtp_transport_rate_delay 16 s, smtp_destination_recipient_limit 1. El límite es global por transporte, no por destino. Postfix conserva su cola en mail-state/spool-postfix. defer_transports=smtp bloquea salida externa hasta autorización operativa. Rspamd realiza filtrado/firma DKIM; milter_default_action=tempfail evita aceptar sin firmante. ClamAV realiza análisis antivirus. El controlador Rspamd escucha sólo en loopback y rechaza inputs privilegiados.

Hostinger publica límite de 5 correos/minuto para VPS; se configura margen de 4/minuto. No es una solución de campañas masivas ni acredita capacidad para 1000 usuarios simultáneos. Con 120 mensajes pendientes se consume aproximadamente la ventana de 30 minutos de recuperación: alertar antes de 50 mensajes / 10 minutos antigüedad y revisar las cuentas failed/cola. SMTP aceptado/sent significa guardado para envío, no entrega en bandeja. Verificar logs de entrega y feedback del destinatario; nunca registrar tokens/cuerpos ni contraseñas.

## DNS y certificados

Valores preparados en .local/dns-required.json del servicio (sin secretos). A mail/webmail→72.61.37.46; MX@→mail.naperu.cloud prioridad10; SPF autoriza sólo estaIP; DKIM selector mail202610; DMARC inicial p=none con informes a soporte, observación antes de endurecer. No publicar AAAA sin IPv6/PTR/routing verificados. PTR 72.61.37.46 debe ser mail.naperu.cloud. Configurar únicamente estos registros en Hostinger; preservar sitio, nameservers y otros proyectos. SMTP propio necesita reputación de IP; configurar DNS no garantiza bandeja de entrada.

Traefik existente tiene HTTPChallenge global: no usar Certbot webroot porque su router interno intercepta ese challenge. Instalar sólo /etc/dokploy/traefik/dynamic/naperu-mail.yml después de validar A/AAAA con Google y Cloudflare. Su resolver existente genera certificado mail con SAN webmail; no cambiar configuración global ni reiniciar Traefik ni tocar ACME compartido. mail-ops.py export-cert lee ese almacén sólo para seleccionar el certificado cuyo domain.main es exactamente mail.naperu.cloud; nunca monta/publica cuentas o certificados ajenos. Valida confianza normal, nombre, keypair y caducidad, sustituye current atómicamente y verifica fingerprint servido tras el detector DMS. Conservar versión válida ante JSON parcial/error. Renovación periódica mediante timer propio.

## Secuencia operativa

1. Ejecutar mail-ops.py init-backup-key; conservar clave privada 600 aparte de los backups cifrados. Al propietario le corresponde conservar copiaoffline independiente; la copia del mismo VPS no protege frente a pérdida del host.
2. Probar scripts/check-smtp.py en STAGE y tests/test_mail_ops.py. Pruebas negativas CA/contraseña/nombre/textoclaro se incluyen también en el gate aislado de la aplicación. Los mensajes técnicos locales se retiran por UID EXPUNGE exacto, preservando cualquier otro mensaje Deleted.
3. Cuando DNS esté listo, public-activate.py routing instala únicamente su router. Esperar TLS normal mail/webmail sin solicitar ACME repetidamente con DNS inválido.
4. public-activate.py publish valida registros y PTR, exporta certificado propio, quita CAstage de Roundcube y abre exclusivamente25/465/587/993 en IPv4. La salida todavía queda diferida.
5. public-activate.py allow-delivery comprueba certificados y elimina defer_transports. Ejecutar check-smtp.py --public --recipient ricardo.rojas.campos@gmail.com: el propietario autorizó únicamente mensajes controlados a esa dirección. Comprobar recepción/envío y cola vacía; no enviar correos a otras personas.
6. Preparar .env de Acrópolis en privado conservando snapshot previo: EMAIL_ENABLED=true, host465/ssl, usuario/from anteriores y contraseña local. Mantener PFX/keyring y proxies. identity-runtime.py --check y --smtp-check; candidato limpio, commit exacto y verify.sh aprobados. Desplegar exclusivamente ese SHA mediante deploy.sh y sus imágenes certificadas, sin rebuild. Si DNS/PTR/entrega fallan, mantener runtimeEmailfalse.
7. Verificar registro real del propietario, confirmación y recuperación mediante su correo designado. No usar semillas QA productivas ni otorgar permisos administrativos. Conservar pruebasHTTP/capabilities, historial y rollback de configuración previa false.

## Respaldo y mantenimiento

mail-ops.py backup detiene/reanuda sólo servicios propios durante snapshot consistente, cifra con GPG AES256+MDC y verifica integridad. Contiene configuración, claves, cuentas, buzones, cola y SQLite Roundcube; backup.key queda fuera. verify-backup y restore-test verifican contenido, modos y UID/GID 33/5000 en directorio nuevo aislado; no sobrescriben estado vivo ni ejecutan Docker restaurado. Backups schema 1 sin ownership deben regenerarse.

Timer diario de respaldo y timer de 5 minutos de exportación/renovación sólo para servicio propio. Revisar systemctl status, fallos de export, expiry, memoria, disco, estado de ClamAV/Rspamd, cola deferred/oldest y rebotes. Rotación de credenciales/DKIM planificada preservando claves públicas antiguas hasta vaciar cola. Restaurar sólo con servicios propios detenidos y plan de datos/claves/imagen compatibles; no borrar volúmenes ni usar limpieza global Docker.

Estado y evidencias vigentes se guardan en .local/ del servicio y en .local/continuation-current.md de Acrópolis, distinguiendo preparado, publicado, aceptadoSMTP y recibido. La guía local de Windows sólo da acceso; DNS-CORREO-PENDIENTE.md es artefacto de revisión, no otra política canónica.
