# Operación de cuentas e identidad

Este documento describe la preparación y los procedimientos. No acredita un despliegue ni la entrega de correo del proveedor real.

## Configuración privada

Trabajar por SSH estricto en /root/proyect/acropolis-channel. SMTP propio está autorizado y separado en el proyecto Naperu Mail; consultar docs/smtp-integration.md, capabilities y manifiesto para su estado vigente. No deshabilitar una integración validada al leer documentación histórica. Para una preparación sin correo puede usarse IDENTITY_EMAIL_ENABLED=false. Al habilitarlo configurar en .env los valores IDENTITY_SMTP_HOST, IDENTITY_SMTP_PORT, IDENTITY_SMTP_SECURITY, IDENTITY_SMTP_USERNAME, IDENTITY_SMTP_PASSWORD, IDENTITY_SMTP_FROM_EMAIL e IDENTITY_SMTP_FROM_NAME. El transporte productivo exige starttls o ssl con validación de certificado. La configuración productiva exige autenticación SMTP: usuario y contraseña deben estar presentes; no se admite un proveedor sin autenticación.

La dirección exacta del propietario se guarda privadamente en el VPS, no en documentación pública. El remitente operativo se configura privadamente. No enviar contraseñas por chat ni registrar las conexiones o los enlaces completos.

Antes del primer despliegue de identidad, ejecutar explícitamente:

```bash
python3 scripts/identity-runtime.py --prepare
python3 scripts/identity-runtime.py --check
```

La preparación crea un protector PFX privado en .local/identity/key-protector.pfx, guarda su contraseña en .env y registra sólo las direcciones exactas de las tareas Traefik en la red configurada. No inicia contenedores ni envía correo. Nunca reemplaza automáticamente un protector configurado que falte: debe recuperarse desde respaldo. El preflight comprueba contraseña, clave privada cifrada y lectura por la identidad app (UID/GID 1654), además de rechazar permisos públicos o escritura del grupo. Un PFX restaurado como root:root 600 debe recuperar sus permisos privados root:1654 640 antes de publicar.

El key ring se conserva en el volumen propio data_protection, montado en /var/acropolis/keys. El usuario app tiene acceso a ese volumen y lectura del PFX mediante un bind privado. Las claves se cifran usando el protector. La identidad estable de Data Protection es AcropolisChannel.

Si Traefik cambia de dirección, repetir la preparación para actualizar las direcciones confiables y aplicar la configuración en el siguiente despliegue autorizado. No confiar en todas las redes o en cabeceras arbitrarias de Internet.

## Despliegue

El flujo exige un commit limpio, QA aprobado y las imágenes exactas. El preflight valida configuración privada, protector y direcciones del proxy, y, únicamente con correo habilitado, ejecuta smtp-check para negociar TLS y autenticar con el proveedor sin enviar correo.

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

La administración requiere MFA después de conceder Users.Manage. La web permite gestionar estado y niveles institucionales; únicamente el propietario protegido puede delegar los tres permisos mediante el endpoint estrecho de permisos. El último administrador activo no puede deshabilitarse.

## Sesiones, correo y observación

Las sesiones duran ocho horas como máximo, sin renovación deslizante. Cambios de contraseña, estado o autorizaciones invalidan las sesiones afectadas en la siguiente petición. Los niveles institucionales no otorgan por sí mismos permisos administrativos.

La confirmación de correo caduca en 24 horas; la recuperación en 30 minutos. Los enlaces usan fragmentos que la interfaz retira después de leerlos y se consumen mediante una operación explícita protegida contra antifalsificación. Reenvío, recuperación y registro no revelan públicamente si una cuenta existe.

El correo se guarda cifrado y se envía desde un outbox con reintentos. Supervisar fallos de entrega y acumulación de pendientes mediante consultas administrativas agregadas; no exportar direcciones, tokens o payloads a logs públicos. La entrega SMTP admite reintentos: un fallo entre aceptación remota y confirmación local puede ocasionar un duplicado, y el enlace sólo puede consumirse una vez.

## Último ingreso individual

El modelo almacena `identity.AccountAccess.LastSignInUtc` como `NOT NULL` cuando existe la fila. La respuesta administrativa puede devolver `lastSignInUtc: null` si la cuenta no tiene un registro histórico; la nulabilidad pertenece al DTO, no a esa columna SQL. La migración no rellena fechas ni crea registros para ingresos anteriores.

La fecha corresponde a un nuevo acceso autenticado válido, con MFA cuando procede. No representa la última visita, reproducción, consulta administrativa ni renovación de sesión. La persistencia y su consulta administrativa están integradas en el código fuente. Esta descripción no acredita validación del commit definitivo ni publicación; consultar el informe de QA y el manifiesto de despliegue correspondientes.

## Pantalla posterior al registro

Después de una respuesta aceptada del registro, la interfaz muestra la dirección introducida y las instrucciones de confirmación, sin pedirla nuevamente. El navegador conserva únicamente el contexto de navegación `{ kind: 'registration', email }` en el historial de esa entrada; permite recargar y volver mediante Atrás/Adelante. No incluye contraseña, tokens ni datos de cuenta, ni escribe el correo en la URL, localStorage o sessionStorage. Este contexto no acredita existencia, autenticación ni confirmación de una cuenta.

El reenvío es una acción secundaria y explícita hacia el endpoint existente. Mantiene el acuse genérico y el límite de un minuto del servidor. Montar, recargar o volver a la pantalla no solicita envíos. Sin contexto válido, los accesos desde login o enlaces inválidos conservan el formulario independiente para introducir una dirección. Los cambios de entrada del historial reinician avisos y errores; la disponibilidad del correo sigue siendo requisito para ofrecer el reenvío.

## Respaldo y recuperación

El respaldo completo requiere el dump de PostgreSQL, el key ring, el protector PFX y la configuración privada necesaria para abrirlo. El despliegue conserva estos materiales en su directorio privado de recuperación, además de las imágenes y configuración previas. candidate.env conserva la configuración preparada, incluida la contraseña del protector; previous.env describe el runtime anterior. No confundir ambos al recuperar una versión. No restaurar el key ring o sustituir su protector como parte de un simple cambio de imagen.

Recuperar una base antigua puede recuperar contraseñas y permisos que habían sido revocados. Restaurar en mantenimiento y ejecutar el procedimiento recovery-invalidate --maintenance antes de permitir tráfico de cuentas. Esto invalida sesiones/enlaces, suspende mensajes restaurados y exige revalidación de las cuentas.

Mantener el servidor web detenido durante la invalidación y los comandos de revalidación. Reconciliar los cambios de seguridad con evidencia independiente posterior al respaldo. Sin ella, conservar bloqueadas las cuentas hasta que el operador autorice individualmente su recuperación.

Para una cuenta revisada:

```bash
docker compose -p acropolis-channel --profile migration run --rm migrations recovery-revalidate --email CORREO_EXACTO --maintenance
```

La revalidación elimina la contraseña y confirmación antiguas, retira IsOwner, Users.Manage, Content.Manage y Subscriptions.Manage, elimina MFA y deja únicamente Externo. Después de permitir el acceso público, el usuario solicita un nuevo correo de confirmación, lo confirma y solicita restablecer su contraseña. Sólo entonces puede iniciar una sesión nueva. Volver a asignar niveles únicamente después de revisarlos. El comando de bootstrap inicial no puede utilizarse para evadir este proceso. Para recuperar una cuenta administradora cuya autoridad haya verificado el operador, el comando separado es recover-admin --email CORREO_EXACTO --maintenance. También elimina la contraseña y confirmación antiguas y exige completar de nuevo confirmación y reset antes de admitir una sesión con Users.Manage. No concede acceso a una cuenta que no estuviera en cuarentena.

Las operaciones administrativas habituales del CLI admiten hasta 30 segundos por comando de PostgreSQL dentro de su plazo total acotado; la invalidación masiva tiene el presupuesto específico descrito abajo. El host HTTP mantiene su límite de tres segundos. La invalidación de una base poblada debe verificarse en QA con el volumen esperado de cuentas antes de recuperar una publicación.

El parámetro --maintenance acredita una acción del operador; no detiene por sí mismo contenedores ni protege contra tráfico activo. Verificar que web está parado antes de usarlo.

Las pruebas de restauración se ejecutan únicamente en proyectos y bases acropolis_test_*. El script restore-db-test.sh rechaza destinos productivos. No realizar migraciones descendentes automáticas.

## Retención operativa

Ejecutar periódicamente como tarea administrativa, cuando corresponda:

```bash
docker compose -p acropolis-channel --profile migration run --rm migrations prune-identity
```

Cada ejecución elimina como máximo 1.000 sesiones vencidas y 1.000 flujos cuyo vencimiento ocurrió hace más de siete días, junto con su outbox asociado. También retira como máximo 1.000 desafíos MFA vencidos o consumidos y 1.000 pruebas contra repetición vencidas. No elimina cuentas, auditorías, credenciales MFA, códigos de recuperación activos ni flujos vigentes. El comando es manual; esta fase no instala un programador externo. Revisar el volumen de datos y definir la frecuencia antes del lanzamiento operativo.


## Modo opcional sin correo

El propietario autorizó después el SMTP propio. La preparación previa sin correo sigue siendo una opción de configuración; no describe necesariamente el runtime vigente. En una publicación autorizada se puede establecer IDENTITY_EMAIL_ENABLED=false en la configuración privada; .env.example ya documenta esta elección. No cambia por sí sola el servicio actualmente desplegado.

El modo deshabilita registro, reenvío de confirmación y solicitud de recuperación con HTTP503/email_unavailable; no responde fingiendo entrega ni confirma cuentas automáticamente. El worker y dispatcher no envían mensajes. Las cuentas previamente confirmadas pueden ingresar y los administradores siguen obligados a completar MFA. La portada y el catálogo público permanecen disponibles. Protector PFX, key ring persistente, TLS, proxy exacto y base privada siguen siendo requisitos.

Para retomar correo, configurar privadamente host/puerto/remitente/usuario/contraseña y STARTTLS o TLS, establecer IDENTITY_EMAIL_ENABLED=true y validar con el flujo normal. El deploy comprueba SMTP sólo cuando está habilitado. Comprobar un correo real controlado antes de declarar entrega operativa. No usar un servidor de QA como proveedor productivo.

La seguridad administrativa se completa con [MFA](mfa-operations.md). El permiso editorial Content.Manage se concede por CLI separado según [catálogo](catalog-operations.md); bootstrap de usuarios no lo concede implícitamente. La revalidación tras restaurar retira permisos editoriales y exige renovar también la configuración MFA conforme al procedimiento.


## Propietario protegido y delegación

La designación exacta se proporciona sólo al CLI mediante Identity__OwnerEmail (Compose migrations lee IDENTITY_OWNER_EMAIL). Mantener ese valor privado. Una cuenta designada debe estar activa y confirmada; no crear confirmaciones, contraseñas o MFA por atajo.

```bash
docker compose -p acropolis-channel --profile migration run --rm migrations bootstrap-owner --email CORREO_EXACTO_DESIGNADO
```

Usar sólo con autorización vigente para aplicar la propiedad, después de migrar una imagen certificada. La operación es idempotente, concurrente, auditable y protege una única propiedad. Marca su bootstrap independiente y rechaza cuarentena/restauración. Concede los tres permisos y revoca sesiones previas, pero no inscribe el autenticador: el propietario completa MFA personalmente. Los demás gestores no pueden suspenderlo ni quitarle autoridad.

PUT /api/v1/admin/users/{id}/permissions admite exclusivamente versión y lista de permisos conocidos; requiere propietario y MFA. La edición habitual de cuenta mantiene un contrato distinto. Un guardado sin cambios reales no revoca sesiones ni genera una falsa modificación; los cambios sensibles sí lo hacen. La protección del último administrador cuenta sólo administradores activos y confirmados.

## Recuperación explícita del propietario

recovery-invalidate elimina propiedad/permisos/MFA restaurados y suspende acceso activo restaurado. recovery-revalidate prepara la cuenta sin contraseña, correo confirmado ni permisos; la persona completa de nuevo confirmación y reset. Sólo tras esos pasos, con web detenido y designación exacta privada, ejecutar recover-owner --email CORREO_EXACTO_DESIGNADO --maintenance. No utilizar bootstrap-owner para evadir recuperación. Mantener MFA obligatorio para toda administración y volver a inscribirlo personalmente.

La auditoría administrativa se consulta sin modificarla y está limitada por permiso, paginación, fechas, acción y cuenta. No publicar secretos, direcciones personales innecesarias ni material MFA en sus registros.

La operación masiva recovery-invalidate --maintenance dispone de un plazo total de cinco minutos. Sus comandos administrativos de cuarentena usan 120 segundos y restauran el timeout anterior al terminar, incluso ante error o cancelación. La cuarentena de cuentas recorre IDs ordenados en lotes de hasta 1.000, dentro de una sola transacción y un único bloqueo administrativo. Cada lote audita exactamente las filas modificadas; una cancelación o un error revierte toda esa operación de identidad, sin conservar lotes parciales. Repetirla no reescribe ni vuelve a auditar cuentas ya en cuarentena. Los tiempos de las peticiones web y sus umbrales de carga no cambian. El servidor permanece en mantenimiento hasta completar la invalidación conjunta de identidad y suscripciones; un error no autoriza abrir el acceso.
