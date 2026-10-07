# Suscripción gratuita y acceso

La etapa free_beta exige pulsar «Suscribirme gratis» desde una cuenta activa y confirmada. No hay tarjeta, cobro, renovación automática o vencimiento artificial. Cualquier contratación de planes futuros requerirá aceptación expresa; esta activación no la sustituye.

## Estados

Una fila por usuario. active admite obras publicadas; cancelled permite reactivar por intención del usuario; suspended sólo puede revisarla un gestor autorizado. La activación concurrente es idempotente y no puede saltar una suspensión administrativa. Cancelación y administración usan versión; un conflicto exige recargar/revisar el estado antes de reintentar.

GET /api/v1/subscriptions/me devuelve estado y elegibilidad. POST /activate con cuerpo vacío activa sólo la cuenta propia; POST /cancel lleva versión. La API no confía en usuario/status enviados desde navegador para asignar acceso. La autorización de consumo combina contratos públicos de Identity, Subscriptions y Catalog.

## Administración

Subscriptions.Manage con MFA permite lista paginada, consulta y cambios de estado con auditoría atómica. El nivel institucional y Users.Manage/Content.Manage no conceden ese permiso. Sólo el propietario delega permisos; otro gestor no puede modificar su acceso. Auditoría de sólo lectura por permiso, fecha, acción y objeto, sin exposición de credenciales o payloads privados.

Subscriptions es dueño de su schema, Subscriptions, Audit e historia EF. Application no conoce PostgreSQL ni Identity interno. Infrastructure puede consultar el contrato público de identidad para verificar cuenta y protección del propietario; no leer ni escribir tablas ajenas. Runtime sin DDL/DELETE ni reescritura de auditorías; runner exclusivo aplica migraciones.

## Reporte de eventos registrados

GET `/api/v1/admin/reports/subscriptions/events?from=YYYY-MM-DD&to=YYYY-MM-DD` exige Subscriptions.Manage y MFA. Sólo admite esas dos fechas, una vez cada una, con formato exacto y un intervalo de una a 366 fechas UTC; `from` es inclusivo y `to` exclusivo. La respuesta es no-store. No entrega identificadores de cuenta o actor ni permite modificar la auditoría.

El reporte agrega exclusivamente filas existentes de `subscriptions.Audit`: activación, reactivación, cancelación, transiciones administrativas y suspensión por recuperación. Eventos que no coinciden con una transición conocida permanecen en `unclassified`. Incluye todos los días del intervalo, también aquellos sin eventos. Su alcance `recorded_events` describe eventos registrados, no cuentas únicas, ingresos, pagos ni el saldo histórico diario de suscriptores. No reconstruye eventos ausentes ni convierte no-ops en nuevas transiciones.

La consulta y la interfaz están integradas en el código fuente. El intervalo máximo no establece una nueva política de eliminación de auditoría ni aprueba tipos, duración o renovación de planes. Esta documentación no acredita el gate del commit definitivo ni su publicación; consultar sus informes y el manifiesto activo.

## Recuperación

Restaurar en mantenimiento invalida identidad/propiedad/permisos/MFA y suspende suscripciones activas restauradas antes de abrir acceso. Revalidar una identidad no reactiva automáticamente una suscripción vieja. Revisar evidencia posterior al backup y recuperar cada acceso explícitamente mediante autoridad validada.

Los tests y cargas usan sólo datos sintéticos en acropolis_test_*. Respaldar esquema, filas/auditoría y material de identidad conjuntamente, comprobar digest antes de invalidación y estado seguro posterior. No ejecutar el procedimiento QA contra producción.

La búsqueda administrativa de cuentas utiliza /api/v1/admin/subscriptions/accounts?search=&page=1&pageSize=20, protegida por Subscriptions.Manage y MFA. Devuelve únicamente identificador, nombre, correo, estado y confirmación para localizar al titular; no concede edición de usuarios ni expone permisos, niveles o versiones. La activación y cancelación personales comparten un límite de 20 solicitudes por minuto y cuenta autenticada, sin colas; las lecturas no consumen ese límite.

La gestión identifica cuentas mediante nombre y correo, con metadatos mínimos autorizados por Subscriptions.Manage y MFA. El lookup por lote acepta como máximo 20 IDs distintos y no vacíos; Identity resuelve una sola consulta y devuelve únicamente id, displayName, email, status y emailConfirmed. No añade niveles, permisos ni marca de propietario. El cliente conserva estos datos sólo en memoria; las URLs de filtro/lookup contienen IDs, y una falla de metadatos no debe borrar cambios de suscripción.

La suspensión masiva durante recuperación utiliza un timeout administrativo de 120 segundos, restaurado al salir de la operación. Forma parte del plazo global de cinco minutos de recovery-invalidate --maintenance; las peticiones web conservan sus límites. La segunda ejecución no reescribe ni vuelve a auditar suscripciones que ya están suspendidas.

La implementación actual ofrece únicamente un piloto gratuito sin vencimiento. Esa simplificación no tiene aprobación humana expresa archivada: confirmar tipos, duración, fechas y renovaciones del documento funcional antes de declarar completa la entrega. El propietario recibió la pregunta concreta; no convertir la ausencia de respuesta en aprobación.
