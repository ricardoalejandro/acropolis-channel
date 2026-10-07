# Entrega de mediateca y backoffice

El alcance de referencia es «Proyecto: Acrópolis Channel v1.0.0» (15 páginas), junto con las indicaciones vigentes del propietario. La entrega solicitada incluye los procesos necesarios para sustituir el sistema anterior; **sólo los pagos están aplazados**. Esta descripción refleja el código integrado en el checkout y sus pendientes de validación y entrega. No declara aprobadas nuevas reglas de negocio, certificadas las imágenes, completa la entrega ni autorizado un despliegue.

La validación definitiva corresponde al gate completo sobre el commit limpio y a sus informes. La existencia de código, pruebas preparadas o diagnósticos de una fuente anterior no demuestra que los recorridos de esta fuente hayan pasado.

## Cuentas y administración

El código incluye perfil, estado, niveles institucionales, propietario protegido, permisos independientes, MFA y último ingreso autenticado válido. El último ingreso registra nuevos accesos; no reconstruye fechas históricas ni equivale a visitas o consumo. La página pública y el backoffice tienen navegación y presentación propias.

El propietario se inicializa mediante una operación administrativa explícita, auditada y protegida por concurrencia para una cuenta exacta, confirmada y activa. Esa operación no confirma correos, cambia contraseñas ni inscribe MFA. Otros gestores no pueden suspenderlo ni quitarle autoridad. `Users.Manage`, `Content.Manage` y `Subscriptions.Manage` son permisos separados; los niveles institucionales tampoco conceden esos permisos.

Pendiente: validar el recorrido administrativo integrado, la delegación y revocación, protección del propietario y aceptación por gestores. Conservar controles de cuenta, confirmación, login, recuperación, MFA y restauración (pp. 5, 8, 10, 13).

## Mediateca y reproducción

Se conservan los seis formatos del sitio oficial: Lecturas, Documentales, Videos, Podcast, Charlas online y Cursos. Búsqueda y filtros combinan formato, texto y tema. Sinopsis y metadatos son públicos; obra completa y referencia multimedia permanecen separadas. La API exige cuenta activa y confirmada, acceso vigente y contenido publicado para entregar la obra o sus referencias ordenadas. Gratuito sólo permite obras marcadas gratuitas; los planes temporales permiten todo el catálogo durante su periodo. Cursos y programas no transfieren su marca gratuita a las obras. Cursos agrupan obras y programas agrupan cursos.

La primera modalidad audiovisual elegida es **YouTube**. El editor acepta URL HTTPS o identificador válido y almacena únicamente el identificador. El usuario inicia la reproducción mediante el reproductor oficial. Un vídeo público en YouTube sigue accesible en su origen; el acceso del portal no demuestra exclusividad de ese recurso. No se extrae audio para convertir un vídeo en podcast.

Pendiente: validar recorridos reales de lectura, reproducción, retiro de publicaciones y cursos/programas en escritorio y móvil. El acceso por nivel institucional requiere las reglas que aún debe confirmar el propietario (pp. 5, 8, 10, 13). AWS es una segunda modalidad futura, más completa; S3 y CloudFront todavía no están configurados. No bloquea esta fase YouTube ni habilita recursos o costos por sí sola.

## Temas y organización editorial

Temas son una clasificación editorial adicional a los seis formatos y a las etiquetas libres. El backoffice preparado permite crear, renombrar, ordenar, archivar, restaurar y asociar temas a obras. No se precarga una taxonomía: empieza sin temas editoriales. El slug permanece estable; archivar un tema conserva sus asociaciones y el contenido. Un tema archivado deja de aparecer en el filtro público y no admite nuevas asociaciones; el editor puede conservar o quitar las existentes explícitamente.

La gestión exige `Content.Manage` y MFA. Versiones de concurrencia protegen tanto la obra como el orden del directorio. La asociación y su auditoría se guardan juntas; ordenar modifica sólo el intervalo afectado. La API pública combina el tema activo con publicación, formato y búsqueda literal y devuelve un listado vacío para un slug válido desconocido o archivado. El límite técnico es de doce temas por obra, reforzado en PostgreSQL.

Pendiente: ejecutar migraciones, paridad de modelo EF, privilegios, rollback y concurrencia en PostgreSQL real; validar edición, filtros, foco, errores y archivo/restauración en el navegador (pp. 4, 8, 13).

## Suscripciones y sus eventos

El propietario confirmó Gratuito sin vencimiento limitado a obras marcadas gratuitas, Probacionismo durante tres meses naturales y Anual durante un año. Se conserva `free_beta` como código compatible del Gratuito. Los planes temporales se asignan y renuevan manualmente con permiso, MFA, razón, fechas y versión, sin cargos ni renovación automática. La cuenta conserva una sola suscripción; la activación propia no convierte un plan manual en Gratuito ni modifica su periodo. Leer [operación de suscripciones](subscriptions-operations.md).

El reporte preparado de eventos agrega la auditoría de Subscriptions por fecha UTC y transición registrada: activación, reactivación, cancelación, asignación, renovación, cambios administrativos y suspensión por recuperación. Incluye eventos no clasificables y días sin eventos dentro del intervalo solicitado, con un máximo de 366 fechas. No inventa historia anterior ni interpreta eventos como cuentas únicas, ingresos, pagos o saldo diario de suscriptores. Es independiente del reporte del estado actual. La consulta exige `Subscriptions.Manage` y MFA; no expone cuentas ni actores.

Las reglas de avisos al titular por asignación, renovación y próximo vencimiento están confirmadas y su implementación está preparada; validación y activación siguen pendientes. También falta ejecutar la validación completa de planes, migraciones, concurrencia y recorridos en la fuente definitiva y obtener la aceptación de gestores (p. 6). La aprobación de negocio no acredita ejecución del gate ni despliegue. El intervalo del reporte de eventos no establece una nueva política de eliminación de auditoría.

## Consumo y reportes

Los reportes de estado actual de cuentas, catálogo y suscripciones siguen separados por módulo. El código preparado añade observaciones de lecturas y YouTube, reportes generales, detalle por obra y consulta por cuenta. Cada módulo consulta sus propios datos; estos reportes no constituyen un snapshot atómico entre módulos.

La política aprobada conserva **90 fechas UTC de detalle diario por cuenta y obra, incluida la actual**, y **365 fechas UTC de estadísticas generales sin identificador de cuenta**. Sesiones y recibos técnicos para reintento permanecen sólo durante su vigencia de ocho horas. La limpieza automática es acotada, idempotente y no reconstruye estadísticas desde detalle caducado. Esta política de consumo no cambia la conservación de otras auditorías.

Una sesión se vincula a la cuenta autenticada y a la versión de seguridad y obra. Cada observación vuelve a comprobar acceso y publicación. El servidor limita el tiempo acreditado, el tamaño del cuerpo y la frecuencia. Recibo y agregados diario por cuenta/general se guardan en la misma transacción; el reintento del mismo cuerpo y secuencia devuelve el recibo sin duplicar cifras. Un conflicto exige una acción explícita del cliente. Cargar una obra o navegar por GET no registra consumo.

Las métricas describen **observaciones aceptadas**, cobertura registrada y tiempo acumulado de sesiones, que puede solaparse. No demuestran atención real. La media de progreso conserva numerador y número de muestras; una muestra desconocida no se convierte en cero. Cuentas distintas se cuentan exactamente sólo dentro del detalle disponible; los intervalos anteriores devuelven `null` y disponibilidad, sin inventar una cifra anual ni sumar únicos diarios.

El reporte general exige `Content.Manage` y MFA. El detalle de una cuenta exige conjuntamente `Users.Manage`, `Content.Manage` y MFA. Las respuestas son `no-store` y evitan correos, material privado y referencias multimedia. El seguimiento está deshabilitado por defecto: la política aprobada no activa su grabación en producción.

Pendiente: ejecutar las pruebas preparadas de reglas, transacciones, replay, retención, autorización, interfaz y recorrido real; comprobar restauración, accesibilidad y etiquetas de disponibilidad. Objetivos por filial, atribución y analítica avanzada no están implementados (pp. 9, 10, 13).

## Validación institucional

Los niveles y controles de seguridad actuales no sustituyen la validación periódica de miembros ni la sincronización con intranet. La revalidación tras restauración protege credenciales y sesiones; no confirma membresía institucional.

Pendiente: identificar la intranet, su contrato y la regla de acceso por nivel; implementar la sincronización y sus fallos sin convertir un nivel en permiso administrativo o suscripción implícita (pp. 6, 8, 10).

## Correo y comunicaciones

Acrópolis conserva su cliente SMTP y mensajes de confirmación y recuperación. La disponibilidad efectiva depende de sus guardas y del runtime activo. El servicio de correo Naperu mantiene fuente y runtime independientes. Las pruebas nuevas usan únicamente correo y cuentas sintéticas aisladas.

El propietario confirmó avisos transaccionales al titular por asignación manual, renovación manual y durante los siete días previos al vencimiento, sin marketing. La implementación está preparada y permanece desactivada; su validación y activación están pendientes. Leer [operación de suscripciones](subscriptions-operations.md). WhatsApp requiere concretar contrato, consentimiento e integración. La entrega previa de confirmación no acredita esos otros mensajes (pp. 7, 8, 10).

## Facturación y gestión comercial

Facturación electrónica sigue solicitada y todavía no está implementada: requiere integrar el mecanismo existente. No se excluye por la gratuidad del piloto ni por aplazar los pagos. Proveedor y contrato API siguen pendientes; no simular comprobantes (pp. 7, 10, 14).

Órdenes, cupones y atribución por filial también siguen pendientes. Precisar condiciones, uso y gestión de operaciones antes de implementar reglas dependientes. Su relación con pagos aplazados no constituye una exclusión adicional aprobada (pp. 7–10, 13).

## Migración, capacidad y lanzamiento

La capacidad prevista de 100.000 cuentas no implica precargarlas en producción. Las cuentas y fichas sintéticas masivas existen únicamente en QA. La carga limitada de 50 sesiones no acredita 1.000 usuarios concurrentes ni distribución audiovisual AWS.

Pendiente: migración o sincronización de contenidos, cuentas y suscripciones; ensayo de recuperación, aceptación funcional, capacitación, piloto e incidentes antes del lanzamiento progresivo. El gate completo debe comprobar la fuente definitiva y sus imágenes inmutables antes de cualquier despliegue solicitado (pp. 10, 14).

## Decisiones abiertas y evolución

Los tipos, duración, alcance y renovación manual de suscripciones ya están confirmados. Quedan por aclarar proveedor y API de facturación y reglas por nivel junto con el contrato de intranet. La ausencia de respuesta no aprueba valores inventados. Sólo los pagos se abordarán después por decisión expresa del propietario.

La app móvil corresponde a fase 5, después de estabilizar la web (p. 14). Favoritos, historial, continuar reproducción, valoraciones/comentarios, reproducción automática, recomendaciones, marketing, push/offline y analítica avanzada figuran como evolución de fase 6 (pp. 14–15). Esa secuencia no elimina los reportes operativos esenciales ni acredita una exclusión adicional.
