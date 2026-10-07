# Suscripciones y acceso al catálogo

El propietario confirmó el 7 de octubre de 2026 estos accesos. La aprobación de sus reglas no acredita las pruebas de la fuente, su publicación ni un despliegue. Consultar el gate y el manifiesto activo.

| Plan | Acceso | Periodo |
| --- | --- | --- |
| Gratuito (`free_beta`) | Sólo obras que un editor marque explícitamente gratuitas. | Sin vencimiento, mientras la suscripción esté activa. |
| Probacionismo (`probationismo`) | Todo el catálogo publicado. | Tres meses naturales desde el inicio indicado por un gestor. |
| Anual (`annual`) | Todo el catálogo publicado. | Un año desde el inicio indicado por un gestor. |

No hay tarjetas, cargos ni renovación automática. Los niveles institucionales y permisos administrativos no conceden una suscripción. Cursos y programas no transfieren acceso gratuito a sus elementos: cada obra comprueba su propia marca.

## Estado efectivo y fechas

Una fila por cuenta conserva plan, estado administrativo, versión, inicio y fin UTC. `effectiveState` distingue active, scheduled, expired, cancelled y suspended. El acceso comienza en `startsUtc` inclusive y termina en `expiresUtc` exclusivo; el vencimiento no cambia silenciosamente el plan a Gratuito ni inventa una transición de auditoría.

El servidor calcula el fin por calendario, sin sustituir tres meses por noventa días ni un año por 365 días. Si el día no existe en el mes o año destino, se usa su último día. PostgreSQL conserva microsegundos; el servicio normaliza a esa precisión antes de persistir y devolver fechas. El backoffice presenta las fechas en hora de Lima y envía UTC.

Por ejemplo, Probacionismo desde el 7 de octubre de 2026 a las 09:00 de Lima termina el 7 de enero de 2027 a las 09:00. Anual desde ese inicio termina el 7 de octubre de 2027 a las 09:00. Al llegar la hora final el acceso temporal vence.

## Activación personal

GET `/api/v1/subscriptions/me` devuelve la suscripción y su elegibilidad. POST `/api/v1/subscriptions/activate`, con cuerpo vacío, activa Gratuito sólo para una cuenta activa y confirmada sin suscripción o con una suscripción gratuita apta. La activación es explícita e idempotente: no crea duplicados, no elude suspensión y no cambia un plan manual ni reinicia sus fechas. POST `/cancel` exige la versión vigente. Un conflicto requiere revisar el estado antes de reintentar.

Activación y cancelación comparten el límite existente de veinte solicitudes por minuto y cuenta autenticada, sin colas. Las lecturas no lo consumen. Una cuenta con un plan manual cancelado, suspendido o vencido requiere revisión de un gestor; su activación gratuita personal no lo sustituye.

## Asignación y renovación manual

`Subscriptions.Manage` con MFA permite localizar una cuenta, consultar y gestionar su suscripción. No se concede con Users.Manage, Content.Manage ni un nivel institucional. Sólo el propietario delega permisos; otro gestor no modifica el acceso del propietario protegido. Cuenta destino activa y confirmada, CSRF, razón y versión se comprueban en servidor.

POST `/api/v1/admin/subscriptions/accounts/{userId}/assign` recibe `{ plan, startsUtc, version, reason }`. Para una nueva fila `version` es null; para una existente debe coincidir. Los planes temporales requieren un inicio explícito; el servidor calcula el fin. Gratuito requiere `startsUtc:null`, establece su inicio inmediato y no acepta una fecha arbitraria. Repetir una asignación gratuita ya activa conserva su inicio y versión sin nueva auditoría.

Para renovar el mismo plan, el periodo solicitado comienza exactamente en el fin vigente. El servicio conserva el inicio original y extiende el fin por tres meses naturales o un año, de modo que renovar antes del vencimiento no interrumpa el acceso actual. Una versión obsoleta devuelve 409 y no vuelve a extender. Repetir la última renovación con la versión actual tampoco duplica extensión o auditoría. No se inventa historia anterior.

Una asignación futura incompatible que sustituiría un periodo actualmente activo devuelve `active_period_would_be_replaced` (409); la fila única no debe recortar silenciosamente ese acceso. Un inicio futuro sin periodo activo sí puede programarse. Cambiar de plan de inmediato, o pasar expresamente a Gratuito, exige versión y razón. No es una renovación automática.

Los cambios y su auditoría se guardan juntos. Nuevos registros incluyen plan y fechas anteriores/posteriores; esos campos pueden ser null en auditoría histórica. Cancelación y suspensión permanecen versionadas; reactivar un estado no prolonga sus fechas.

## Avisos transaccionales al titular

El propietario confirmó el 7 de octubre de 2026 avisos por correo al titular cuando un gestor asigne o renueve su plan y antes del vencimiento de un plan temporal, con una ventana de siete días. No son mensajes de marketing. Una asignación manual a Gratuito informa acceso sólo a obras marcadas gratuitas y ausencia de vencimiento; la activación personal gratuita no genera estos avisos.

Asignación o renovación con cambio real guarda su aviso en `subscriptions.NotificationOutbox` dentro de la misma transacción que la fila y su auditoría. No-ops, versiones obsoletas o una transacción revertida no generan otro aviso. Cada mensaje conserva un identificador estable y referencia al evento o generación del periodo, sin persistir correo, cuerpo ni tokens.

El recordatorio sólo se programa para un periodo temporal activo ya iniciado cuyo fin aún no llegó y está a siete días o menos. La selección excluye las generaciones ya registradas antes de aplicar el lote de cien, para que no bloqueen las siguientes. Una ejecución omitida puede recuperarse dentro de esa ventana; no se envía un recordatorio vencido ni se afirma que siempre faltan exactamente siete días. Cambios reales del periodo rotan su generación interna; una suspensión o reactivación administrativa y los no-ops la conservan. Ese dato técnico no se expone en las APIs de suscripción.

### Configuración y transporte

`SUBSCRIPTIONS_NOTIFICATIONS_ENABLED=false` es el valor por defecto de `.env.example` y de Compose web; se traduce a `Subscriptions__Notifications__Enabled`. Desactivado impide encolar, programar y enviar nuevos avisos, evitando una cola histórica generada durante ese modo. Correo de Identity deshabilitado también impide esas operaciones. Cambiar este valor privado, publicar código o pasar QA no activa por sí solo producción: seguir el despliegue solicitado y su gate del commit definitivo.

El cliente comparte el transporte autenticado de Identity, su configuración SMTP y validación TLS; no duplica credenciales ni instala correo. El remitente, nombre visible y `PublicOrigin` proceden de esa configuración validada. Los nuevos avisos usan Message-ID estable con dominio del remitente; confirmación y recuperación conservan sus identificadores y enlaces actuales. Antes de enviar, Identity resuelve únicamente el correo actual de una cuenta activa, confirmada y sin revalidación pendiente mediante su contrato público. Subscriptions no consulta tablas internas de Identity. La fila y sus términos también se revalidan para cancelar un aviso obsoleto. Los mensajes presentan las fechas persistidas en hora de Lima (UTC-05:00) y enlazan `/profile/subscription`, sin tokens.

El worker revisa cada quince segundos, con lote máximo de cien y un plazo de veinte segundos por iteración. Un presupuesto PostgreSQL compartido entre instancias de avisos limita el inicio de un intento SMTP a uno cada quince segundos. No constituye una segunda cuota de entregas externas: ambas colas consumen el servicio de correo y su límite global de salida descrito en `docs/smtp-integration.md`. El bundle operativo instalado configura un único transporte con intervalo de dieciséis segundos; comprobar su runtime al activar, sin inferirlo sólo de archivos ni modificar el proyecto independiente de correo desde Acrópolis.

El claim usa bloqueo y lease de treinta segundos; SMTP tiene un plazo de quince segundos y hasta cinco intentos totales con espera creciente. Los terminales sent, failed y cancelled no se reclaman. SMTP opera fuera de la transacción: si se acepta el mensaje y el proceso cae antes de registrar el resultado, puede repetirse con el mismo Message-ID. No se promete entrega exactamente una vez ni llegada a la bandeja por una aceptación SMTP. Logs y errores no exponen destinatarios, cuerpo, credenciales ni detalles privados del proveedor.

### QA, recuperación y conservación

En Testing el worker permanece inactivo aunque se habiliten los avisos, salvo opt-in explícito `Subscriptions__Notifications__RunInTesting=true` en el entorno sintético de QA. No trasladar esa opción a Compose productivo ni usar datos o correo reales. Verificar PostgreSQL/HTTP, doble worker con scope reutilizado, atomicidad de fila/audit/outbox, flags de correo, no-op/conflicto, ventana de siete días y lotes, exclusión de términos obsoletos, leases, límite compartido, fallos/reintentos y SMTP aislado con confianza TLS normal. Conservar las pruebas de confirmación, recuperación y MFA; no rebajar sus controles.

Durante `recovery-invalidate --maintenance`, con web detenido, la misma transacción de Subscriptions cancela todos los estados de outbox restaurados, limpia leases y registra un tombstone del periodo temporal actual aunque el backup aún no tuviera recordatorio. Revalidar la cuenta o reactivar sólo el estado no reenvía ese periodo. Una posterior asignación o renovación genuina crea una nueva generación y admite su propio aviso. Una falla revierte conjuntamente esos cambios y la suspensión/auditoría existentes.

Se conserva el ledger técnico mínimo necesario para deduplicación y supresión tras recuperación; no contiene contenido del correo, dirección ni tokens. No aplicar a este historial los plazos de 90 y 365 fechas del consumo ni inventar una política de eliminación. El downgrade rechaza un historial de avisos existente para evitar perder su idempotencia.

## Consulta administrativa y privacidad

La búsqueda `/api/v1/admin/subscriptions/accounts?search=&page=1&pageSize=20` usa Subscriptions.Manage y MFA. Devuelve identificador, nombre, correo, estado y confirmación para localizar al titular; no concede edición de usuarios ni expone permisos, niveles o versiones. El lookup por lote acepta como máximo veinte IDs distintos y no vacíos; Identity los resuelve mediante su contrato público y una consulta.

El cliente conserva esos metadatos sólo en memoria. Una falla de metadatos no borra cambios pendientes. Auditoría de sólo lectura, paginada y filtrable por permiso, fecha, acción y objeto; no muestra credenciales ni payloads privados.

Subscriptions es dueño de su schema, filas, auditoría e historia EF. Application no conoce PostgreSQL ni tablas internas de Identity. Infrastructure consulta únicamente el contrato público de identidad para comprobar cuenta y protección del propietario. Runtime sin DDL/DELETE ni reescritura de auditoría; el runner exclusivo aplica migraciones.

## Reporte de eventos registrados

GET `/api/v1/admin/reports/subscriptions/events?from=YYYY-MM-DD&to=YYYY-MM-DD` exige Subscriptions.Manage y MFA. Sólo admite esas dos fechas una vez cada una, formato exacto y de una a 366 fechas UTC; from inclusivo y to exclusivo. Respuesta no-store, sin cuentas ni actores y sin edición de auditoría.

El reporte agrega filas reales de `subscriptions.Audit`: activación, reactivación, cancelación, asignación, renovación, transiciones administrativas y suspensión por recuperación. Los eventos desconocidos siguen en unclassified. Incluye días sin movimientos. `recorded_events` significa eventos registrados, no personas únicas, pagos, ingresos ni saldo histórico diario. No reconstruye eventos ausentes ni convierte no-ops o vencimientos calculados en nuevas transiciones. El intervalo consultable no define retención de auditoría.

## Recuperación y validación

Restaurar en mantenimiento invalida identidad, propiedad, permisos y MFA, y suspende las suscripciones activas restauradas antes de abrir acceso. Revalidar una identidad no reactiva automáticamente un acceso antiguo. Revisar evidencia posterior al backup y recuperar cada acceso expresamente mediante autoridad validada.

La suspensión masiva conserva el timeout administrativo de 120 segundos dentro del plazo global de cinco minutos de recovery-invalidate --maintenance; se restaura al salir. Repetirla no modifica ni vuelve a auditar las filas ya suspendidas. Las peticiones web mantienen sus propios límites.

Pruebas y cargas sólo con datos sintéticos en acropolis_test_*. Verificar reglas de calendario, límites de inicio/fin, autorización, lectura/start/pulse/replay, concurrencia, rollback/auditoría, interfaz y navegador. Respaldar esquema, filas/auditoría y material de identidad conjuntamente; comparar digest antes de invalidación y comprobar estado seguro posterior. No ejecutar QA destructiva sobre producción.
