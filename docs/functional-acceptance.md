# Aceptación funcional del incremento

Esta guía traduce el [alcance funcional](modernization-scope.md) a recorridos verificables para el propietario y los gestores. Describe el incremento integrado; **no afirma que sus pruebas hayan pasado ni que esté desplegado**. La aceptación de este incremento tampoco declara completa la sustitución del sistema anterior.

## Recorridos de aceptación

Realizar estos recorridos con cuentas, obras y correo sintéticos en QA aislada. Preparar visitante, suscriptor, gestor de cada permiso y propietario de QA con MFA; no inicializar al propietario real como parte de estas pruebas.

| Recorrido | Resultado que debe comprobarse | Condición o fallo crítico |
| --- | --- | --- |
| Cuenta y correo | Registrar, ver «Revisa tu correo» con la dirección utilizada, confirmar mediante el enlace, ingresar y recuperar contraseña. Reenvío opcional sin repetir la dirección después del registro. | Navegar, recargar o volver atrás no envía correo. Reenvío explícito respeta 60 segundos; comprobar dos mensajes con IDs distintos en SMTP de QA. Token usado o inválido no concede acceso. Correo no disponible bloquea emisión sin atajos. |
| MFA y delegación | Completar el segundo factor; el propietario de QA delega y revoca Usuarios, Contenidos y Suscripciones por separado. El menú acompaña los permisos efectivos. | Probar también petición HTTP directa sin permiso o MFA. Niveles institucionales no conceden autoridad. Otros gestores no suspenden ni quitan autoridad al propietario; cambios sensibles revocan las sesiones correspondientes. |
| Catálogo editorial | Crear borrador, editar, publicar, retirar y archivar; buscar y paginar. Conservar los seis formatos oficiales, etiquetas y dirección publicada estable. | Borrador/archivo no se exponen públicamente. Un conflicto de versión conserva la edición; salir con cambios pide confirmación. Una respuesta tardía no devuelve al gestor a una pantalla abandonada. |
| Temas | Crear, renombrar, ordenar y asociar hasta doce temas a una obra. Combinar tema, formato y búsqueda; archivar y restaurar. | Archivo conserva obra y asociaciones; tema archivado no admite nuevas asociaciones. Filtro público con tema válido desconocido/archivado queda vacío. Concurrencia no pierde selección ni duplica auditoría. |
| Lecturas, YouTube y cursos | Ver sinopsis pública; con cuenta confirmada, activa y suscripción activa acceder a lectura completa o reproductor oficial. Pegar URL HTTPS/ID válido en el editor. Recorrer cursos y programas en su orden. | Sin acceso no se entrega obra ni referencia restringida. Reproducción sólo por elección; mostrar error si YouTube no permite reproducir. Retirar la publicación impide nuevas entregas; observaciones contra una versión anterior se rechazan. No extraer audio ni afirmar exclusividad del vídeo. |
| Suscripción piloto | Activar explícitamente el piloto, cancelar y reactivar; gestionar suspensión con permiso y MFA. Comprobar acciones repetidas y conflicto de versión. | Una cuenta no crea suscripciones duplicadas ni elude una suspensión administrativa. `free_beta` sin vencimiento es una simplificación técnica pendiente de aprobación; este recorrido no acepta las reglas definitivas de planes. |
| Estado, eventos y último ingreso | Consultar reportes por permiso; distinguir estado actual, movimientos de suscripción por fechas UTC y último ingreso autenticado válido. | Eventos incluyen días sin movimientos y no representan ingresos monetarios, saldo diario ni personas únicas. Máximo 366 fechas consultadas no modifica retención de auditoría. Fallos de login y visitas no actualizan último ingreso ni inventan historia anterior. |
| Consumo observado | En QA con registro habilitado, observar lectura visible y reproducción iniciada; consultar resumen, obra y cuenta. Reintentar explícitamente una consulta fallida conserva sus fechas y página. | Un reintento idéntico no duplica cifras; pausa, salto, pérdida de foco y duración desconocida no fabrican avance. General requiere Contenidos+MFA; cuenta requiere además Usuarios. Registro deshabilitado no impide consumir la obra. |

En escritorio y móvil comprobar teclado, foco, textos largos, carga, errores accesibles, ausencia de desbordamiento y bloqueo de peticiones duplicadas. Un fallo de red, 429 o 503 conserva una salida/reintento comprensible, sin enviar correo por navegar ni perder cambios silenciosamente. El colector de observaciones mantiene sus reintentos técnicos acotados.

## Mapa de pruebas y evidencia

Son pruebas existentes o preparadas en la fuente; su presencia no acredita ejecución. El [gate de calidad](quality.md) exige reglas/componentes, HTTP, PostgreSQL real, autorización y fallos, además de navegador y cobertura. Registrar resultado, SHA y recorrido probado; una comprobación pendiente se conserva como pendiente.

| Área | Navegador | Reglas, contratos y persistencia |
| --- | --- | --- |
| Cuenta, correo y MFA | [identity](../frontend/e2e/identity.spec.ts), [mfa](../frontend/e2e/mfa.spec.ts), [correo deshabilitado](../frontend/e2e/email-deferred.spec.ts) | [Identity](../tests/backend/Acropolis.Identity.IntegrationTests), [componentes de cuentas](../frontend/src/features/accounts), [Modernization](../frontend/src/Modernization.test.tsx) |
| Catálogo y Temas | [catalog](../frontend/e2e/catalog.spec.ts), [topics](../frontend/e2e/topics.spec.ts) | [Catalog](../tests/backend/Acropolis.Catalog.IntegrationTests), [Topic HTTP](../tests/backend/Acropolis.Api.Tests/TopicHttpTests.cs), [TopicsAdmin](../frontend/src/features/catalog/TopicsAdmin.test.tsx) |
| Obras, cursos y piloto | [modernization](../frontend/e2e/modernization.spec.ts), [URL YouTube](../frontend/e2e/youtube-url.spec.ts) | [acceso a obra](../tests/backend/Acropolis.Catalog.IntegrationTests/ConsumptionHttpTests.cs), [Subscriptions](../tests/backend/Acropolis.Subscriptions.IntegrationTests) |
| Reportes e ingreso | [reports](../frontend/e2e/reports.spec.ts), [subscription-events](../frontend/e2e/subscription-events.spec.ts), [last-sign-in](../frontend/e2e/last-sign-in.spec.ts) | [contratos HTTP](../tests/backend/Acropolis.Api.Tests), [componentes de reportes](../frontend/src/features/admin) |
| Consumo y retención | [consumption-activity](../frontend/e2e/consumption-activity.spec.ts) | [reglas](../tests/backend/Acropolis.Catalog.UnitTests/ConsumptionActivityRulesTests.cs), [PostgreSQL](../tests/backend/Acropolis.Catalog.IntegrationTests/ConsumptionActivityTests.cs), [worker](../tests/backend/Acropolis.Api.Tests/ConsumptionRetentionWorkerTests.cs), [colector](../frontend/src/features/catalog/activityRecorder.test.ts) |

La QA utiliza recursos temporales `acropolis_test_*`, SMTP/CA propios y datos sintéticos. Mantener migraciones y restauración con propiedad/ACL, digest completo y escritores detenidos. Confirmar el gate del commit limpio y sus imágenes exactas antes de publicar o desplegar conforme al procedimiento existente.

## Límites y pendientes de la entrega

El [registro de consumo](consumption-activity-operations.md) conserva 90 fechas UTC de detalle por cuenta/obra, incluida la actual, 365 fechas generales sin cuenta y sesiones/recibos técnicos durante ocho horas. Las cifras son observaciones aceptadas: tiempo de sesiones puede solaparse, cobertura no prueba atención y duración desconocida no equivale a cero. Cuentas distintas sólo dentro del detalle disponible; un periodo anterior muestra «No disponible». Habilitar registro en QA no lo activa en producción.

Siguen pendientes las tres decisiones de negocio: proveedor/API de facturación electrónica; planes, duración y renovaciones; reglas de acceso institucional y contrato de intranet. Facturación permanece en alcance. También faltan integración institucional, migración de cuentas/contenidos/suscripciones del sistema anterior, comunicaciones de vencimiento, órdenes, cupones y atribución por filial según el documento funcional. **Sólo los pagos se aplazan por decisión expresa.**

AWS será una modalidad futura: S3/CloudFront todavía no están configurados. La capacidad de 100.000 cuentas no significa precargarlas; la carga limitada de QA no certifica 1.000 concurrentes. Antes del lanzamiento, completar pendientes de alcance, aceptación de gestores, recuperación y publicación autorizada; no considerar este incremento suficiente por sí solo para sustituir WordPress.
