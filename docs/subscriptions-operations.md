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
