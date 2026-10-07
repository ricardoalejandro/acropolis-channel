# Actividad de consumo registrada

Este candidato añade observaciones de lecturas y del reproductor oficial de YouTube, incluidos los podcasts que actualmente se reproducen en YouTube. No incorpora AWS ni una fuente de audio HTML distinta. Una sesión de lectura significa que se entregó la obra autorizada; su cobertura indica exposición de partes del texto, no comprensión. Las señales del reproductor proceden del cliente: no verifican atención humana ni transforman vídeos públicos de YouTube en recursos exclusivos.

## Autorización y recogida

El frontend consulta capacidades autenticadas y sólo crea una sesión después de la entrega autorizada efectiva de una lectura o del inicio solicitado por la persona en YouTube. Abrir una ficha pública, montar un iframe o recibir READY no genera una sesión. `Catalog:Consumption:RecordingEnabled` es false por defecto; `Catalog__Consumption__RecordingEnabled=true` debe configurarse explícitamente en QA. La publicación y activación productiva siguen el procedimiento del proyecto.

Cada escritura vuelve a comprobar cuenta activa y confirmada, suscripción activa, publicación y versión de la obra. El host compone los contratos públicos de Identity, Subscriptions y Catalog; Catalog sólo consulta su schema. Una sesión queda vinculada al hash de la versión de autenticación actual y no sirve para reanudar un ticket restaurado tras la revalidación de seguridad. Las escrituras requieren antiforgery, están limitadas a treinta por cuenta y minuto y admiten como máximo 8192 bytes, también sin Content-Length. Las respuestas privadas, incluidos los errores, son no-store.

La vigencia de una sesión es de ocho horas desde StartedUtc. VisitId admite un inicio idempotente dentro de esa vigencia, con la misma obra, versión y autenticación. Cada secuencia guarda un hash del cuerpo normalizado y su recibo; el reenvío exacto devuelve el mismo recibo y no incrementa nada. Reutilizar una secuencia con datos distintos o saltar la siguiente produce conflicto. Tras vencer y purgar, la aplicación inicia una visita nueva; no se promete conservar claves de reintento indefinidamente.

El servidor acota tiempo acreditado al menor de actividad declarada, intervalo y tiempo transcurrido del servidor, con máximo de quince segundos. Los rangos multimedia tampoco pueden exceder el crédito multiplicado por la velocidad. El colector debe descontar pausas, cambios de foco/visibilidad, lagunas y saltos; el servidor no concede un margen adicional como tiempo. ENDED es un evento separado y nunca fuerza 100%. Duración ausente produce porcentaje desconocido; una variación mayor de un segundo deja el progreso desconocido durante esa sesión. La cobertura es una unión sin rellenar saltos, con máximo de 512 fragmentos; si se alcanza el límite se explicita cobertura incompleta.

## Retención aprobada y métricas

La elección vigente del propietario es noventa días de detalle por cuenta y un año de estadísticas generales, sustituyendo la selección inicial de treinta días. La precisión técnica son **90 fechas UTC incluida hoy**, desde hoy menos 89 días, y **365 fechas UTC incluida hoy**, desde hoy menos 364 días. No son 90 intervalos móviles de veinticuatro horas ni una promesa de preservar cualquier año bisiesto completo. El reloj y las fechas de registro los fija el servidor.

`ConsumptionAccountDaily` conserva métricas por cuenta, obra, versión, formato y fecha durante 90 fechas UTC. `ConsumptionDaily` conserva las mismas métricas generales durante 365 fechas y no contiene identificadores de cuenta, sesión o autenticación. Ambos agregados se actualizan junto al recibo y estado de sesión en una transacción: un fallo revierte todo. La purga elimina detalle vencido y recibos; nunca reconstruye ni vuelve a sumar estadísticas desde tablas parciales.

Se conservan numeradores y denominadores: comienzos, pulsos, milisegundos acreditados, primeros avisos ENDED por sesión, muestras con porcentaje conocido/desconocido y suma de puntos básicos. El promedio divide la suma por el número de muestras conocidas sólo al presentar el resultado; es promedio de muestras de progreso, no avance final medio de personas. El tiempo acumula intervalos de sesiones y puede solaparse entre pestañas o dispositivos; no acredita una unión de uso ni concurrencia certificada.

Las cuentas con actividad son DISTINCT de sesiones admitidas y observaciones disponibles dentro de la ventana de detalle. El comienzo de lectura no significa lectura completa. Para intervalos que empiezan antes de `DetailAvailableFromUtc`, esa cifra es null; no se suman cuentas distintas de días/obras para inventar personas únicas anuales. Las métricas generales aditivas continúan disponibles en su ventana de 365 fechas.

## Consultas y limpieza

Los informes requieren Content.Manage y MFA; el detalle de una cuenta exige además Users.Manage y utiliza el contrato de Identity para verificarla. No se añaden permisos ni excepciones para propietario. Las consultas aceptan `from` y `to` como fechas ASCII exactas, con intervalo UTC [from,to) de 1–366 fechas; página 1–1000000 y tamaño 1–100. Desconocidos, duplicados o fechas inválidas se rechazan. Daily contiene todas las fechas solicitadas, con ventanas explícitas `AvailableFromUtc` y `DetailAvailableFromUtc`: cero fuera de disponibilidad significa cero registrado disponible, no prueba de que no hubo consumo real. Las fichas paginadas usan orden estable y la sinopsis actual del catálogo propio, sin texto restringido ni referencia YouTube.

El worker de retención se inicia también cuando el registro está deshabilitado. En Testing no abre scopes ni conexiones salvo `EnableRetentionInTesting=true`, opt-in reservado a pruebas específicas; producción ignora ese interruptor de pruebas. Cada ciclo tiene presupuesto de treinta segundos, máximo de diez lotes y timeout SQL de tres segundos por comando; los ciclos se separan por un minuto. Cada lote borra como máximo mil recibos, mil sesiones ya sin hijos, mil detalles diarios por cuenta y mil agregados vencidos. SKIP LOCKED permite limpieza concurrente; primero se borran recibos para evitar cascadas masivas. Cancelación o fallo revierte el lote. Los logs contienen sólo categorías de fallo, nunca cuentas, SQL, cuerpos o conexiones. La readiness existente sigue comprobando los historiales y conectividad; estos límites de mantenimiento no certifican capacidad ni ausencia de atraso.

## Respaldo y validación pendiente

La migración propia 20261007043000_AddConsumptionActivity sigue AddTopics. Crea cuatro tablas vacías sin backfill; no modifica Users ni auditoría editorial. El migrador conserva propiedad de Catalog y el runtime no puede actualizar recibos; DELETE permanece disponible para la retención acotada.

La propuesta de digest incluye las cuatro tablas de consumo y las cuatro de Temas, bajo una instantánea REPEATABLE READ READ ONLY UTC. Para comparar antes de backup, después de restore y antes/después de recovery-invalidate, detener y verificar los escritores web/worker aislados de QA. Un reloj fijo por sí solo no detiene escrituras ni purgas. Capturar explícitamente el exit del digest antes de comparar; nunca aceptar un hash igual si el comando falló. La revalidación de Identity conserva las métricas y descarta la utilidad de bindings antiguos. No ejecutar restauración o purga contra producción para estas pruebas.

Esta fuente está preparada, sin compilar ni ejecutar. Quedan pendientes metadatos EF manuales/HasPendingModelChanges, PostgreSQL real, HTTP y worker, colector/interfaz, recorridos de navegador y gate limpio del SHA definitivo. No certifica imágenes ni 1000 concurrentes.

## Dimensionamiento

Como ejemplo de dimensionamiento, mil sesiones que emitan cada diez segundos de forma sostenida generan hasta 8,64 millones de pulsos por día; conservarlos durante noventa días sería aproximadamente 778 millones. Este diseño conserva recibos durante ocho horas (hasta 2,88 millones bajo ese ejemplo), y agrega el detalle por cuenta/obra/fecha. No es una carga medida ni una garantía de actividad continua.

Diez lotes de mil recibos por minuto representan un techo teórico de diez mil borrados/minuto frente a seis mil recibos/minuto en ese ejemplo. El presupuesto real de treinta segundos y el timeout SQL pueden reducir ese ritmo; hay que medir almacenamiento, WAL, índices, DISTINCT, latencia y atraso de purga con el volumen previsto antes de certificar mil concurrentes. El objetivo de cuentas y fichas no es una precarga productiva. El reporte no inventa cifras previas al comienzo del registro.
