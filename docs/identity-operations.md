# Operación de cuentas e identidad

Este documento describe la preparación y los procedimientos. No acredita un despliegue ni la entrega de correo del proveedor real.

## Configuración privada

Trabajar por SSH estricto en /root/proyect/acropolis-channel. Configurar en .env los valores IDENTITY_SMTP_HOST, IDENTITY_SMTP_PORT, IDENTITY_SMTP_SECURITY, IDENTITY_SMTP_USERNAME, IDENTITY_SMTP_PASSWORD, IDENTITY_SMTP_FROM_EMAIL e IDENTITY_SMTP_FROM_NAME. El transporte productivo exige starttls o ssl con validación de certificado. La configuración productiva exige autenticación SMTP: usuario y contraseña deben estar presentes; no se admite un proveedor sin autenticación.

La identidad del remitente y el correo del primer administrador siguen pendientes del propietario. No enviar contraseñas por chat ni registrar las conexiones o los enlaces completos.

Antes del primer despliegue de identidad, ejecutar explícitamente:

```bash
python3 scripts/identity-runtime.py --prepare
python3 scripts/identity-runtime.py --check
```

La preparación crea un protector PFX privado en .local/identity/key-protector.pfx, guarda su contraseña en .env y registra sólo las direcciones exactas de las tareas Traefik en la red configurada. No inicia contenedores ni envía correo. Nunca reemplaza automáticamente un protector configurado que falte: debe recuperarse desde respaldo. El preflight comprueba contraseña, clave privada cifrada y lectura por la identidad app (UID/GID 1654), además de rechazar permisos públicos o escritura del grupo. Un PFX restaurado como root:root 600 debe recuperar sus permisos privados root:1654 640 antes de publicar.

El key ring se conserva en el volumen propio data_protection, montado en /var/acropolis/keys. El usuario app tiene acceso a ese volumen y lectura del PFX mediante un bind privado. Las claves se cifran usando el protector. La identidad estable de Data Protection es AcropolisChannel.

Si Traefik cambia de dirección, repetir la preparación para actualizar las direcciones confiables y aplicar la configuración en el siguiente despliegue autorizado. No confiar en todas las redes o en cabeceras arbitrarias de Internet.

## Despliegue

El flujo exige un commit limpio, QA aprobado y las imágenes exactas. El preflight valida configuración privada, protector y direcciones del proxy, y ejecuta smtp-check para negociar TLS y autenticar con el proveedor sin enviar correo.

```bash
bash scripts/verify.sh
bash scripts/deploy.sh --expected-sha SHA_VALIDADO
```

La migración de búsqueda instala pg_trgm en identity y crea dos índices GIN dentro de PostgreSQL; no instala herramientas en el host ni cambia otras bases. Requiere CREATE en la base propia para acropolis_migrator, ya previsto para el propietario de la base; acropolis_app conserva sus permisos sin DDL. La construcción normal de índices puede bloquear escrituras brevemente en una tabla poblada, por lo que debe medirse en QA antes de aplicar. Un respaldo conserva la extensión y los índices; tras restaurarlo, ejecutar el migrador de la imagen correspondiente antes de abrir tráfico.

No ejecutar el segundo comando hasta que el despliegue esté solicitado. La conexión SMTP correcta no prueba entrega en bandeja de entrada: comprobar la confirmación y recuperación con un correo controlado por el propietario al habilitar el sistema.

## Primer administrador

La persona designada se registra y confirma su correo mediante los flujos normales. Después, ejecutar privadamente:

```bash
docker compose -p acropolis-channel --profile migration run --rm migrations bootstrap-admin --email CORREO_CONFIRMADO
```

La operación concede Users.Manage sólo al usuario exacto, confirmado y activo, bajo un bloqueo que impide dos inicializaciones simultáneas. Rechaza la operación si ya existe un administrador. No crea contraseñas ni constituye un mecanismo de recuperación administrativa.

La administración web permite gestionar estado y niveles institucionales, pero no otorgar permisos administrativos. El último administrador activo no puede deshabilitarse.

## Sesiones, correo y observación

Las sesiones duran ocho horas como máximo, sin renovación deslizante. Cambios de contraseña, estado o autorizaciones invalidan las sesiones afectadas en la siguiente petición. Los niveles institucionales no otorgan por sí mismos permisos administrativos.

La confirmación de correo caduca en 24 horas; la recuperación en 30 minutos. Los enlaces usan fragmentos que la interfaz retira después de leerlos y se consumen mediante una operación explícita protegida contra antifalsificación. Reenvío, recuperación y registro no revelan públicamente si una cuenta existe.

El correo se guarda cifrado y se envía desde un outbox con reintentos. Supervisar fallos de entrega y acumulación de pendientes mediante consultas administrativas agregadas; no exportar direcciones, tokens o payloads a logs públicos. La entrega SMTP admite reintentos: un fallo entre aceptación remota y confirmación local puede ocasionar un duplicado, y el enlace sólo puede consumirse una vez.

## Respaldo y recuperación

El respaldo completo requiere el dump de PostgreSQL, el key ring, el protector PFX y la configuración privada necesaria para abrirlo. El despliegue conserva estos materiales en su directorio privado de recuperación, además de las imágenes y configuración previas. candidate.env conserva la configuración preparada, incluida la contraseña del protector; previous.env describe el runtime anterior. No confundir ambos al recuperar una versión. No restaurar el key ring o sustituir su protector como parte de un simple cambio de imagen.

Recuperar una base antigua puede recuperar contraseñas y permisos que habían sido revocados. Restaurar en mantenimiento y ejecutar el procedimiento recovery-invalidate --maintenance antes de permitir tráfico de cuentas. Esto invalida sesiones/enlaces, suspende mensajes restaurados y exige revalidación de las cuentas.

Mantener el servidor web detenido durante la invalidación y los comandos de revalidación. Reconciliar los cambios de seguridad con evidencia independiente posterior al respaldo. Sin ella, conservar bloqueadas las cuentas hasta que el operador autorice individualmente su recuperación.

Para una cuenta revisada:

```bash
docker compose -p acropolis-channel --profile migration run --rm migrations recovery-revalidate --email CORREO_EXACTO --maintenance
```

La revalidación elimina la contraseña y confirmación antiguas, retira Users.Manage y deja únicamente Externo. Después de permitir el acceso público, el usuario solicita un nuevo correo de confirmación, lo confirma y solicita restablecer su contraseña. Sólo entonces puede iniciar una sesión nueva. Volver a asignar niveles únicamente después de revisarlos. El comando de bootstrap inicial no puede utilizarse para evadir este proceso. Para recuperar una cuenta administradora cuya autoridad haya verificado el operador, el comando separado es recover-admin --email CORREO_EXACTO --maintenance. También elimina la contraseña y confirmación antiguas y exige completar de nuevo confirmación y reset antes de admitir una sesión con Users.Manage. No concede acceso a una cuenta que no estuviera en cuarentena.

Las operaciones administrativas del CLI admiten hasta 30 segundos por comando de PostgreSQL dentro de su plazo total acotado; el host HTTP mantiene su límite de tres segundos. La invalidación de una base poblada debe verificarse en QA con el volumen esperado de cuentas antes de recuperar una publicación.

El parámetro --maintenance acredita una acción del operador; no detiene por sí mismo contenedores ni protege contra tráfico activo. Verificar que web está parado antes de usarlo.

Las pruebas de restauración se ejecutan únicamente en proyectos y bases acropolis_test_*. El script restore-db-test.sh rechaza destinos productivos. No realizar migraciones descendentes automáticas.

## Retención operativa

Ejecutar periódicamente como tarea administrativa, cuando corresponda:

```bash
docker compose -p acropolis-channel --profile migration run --rm migrations prune-identity
```

Cada ejecución elimina como máximo 1.000 sesiones vencidas y 1.000 flujos cuyo vencimiento ocurrió hace más de siete días, junto con su outbox asociado. No elimina cuentas, auditorías ni flujos vigentes. El comando es manual; esta fase no instala un programador externo. Revisar el volumen de datos y definir la frecuencia antes del lanzamiento operativo.
