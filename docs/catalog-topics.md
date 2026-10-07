# Temas del catálogo

Los seis formatos editoriales —Lecturas, Documentales, Videos, Podcast, Charlas online y Cursos— se conservan. Temas es un directorio plano y opcional, separado de esos formatos y de las etiquetas libres. No se crean términos predefinidos.

## Mantenimiento

`Content.Manage` con MFA permite crear, renombrar, ordenar, archivar y restaurar temas. La dirección del tema se fija al crearlo; su nombre admite entre 2 y 180 caracteres sin controles. El directorio admite búsqueda literal y paginación, con estados activo y archivado. El orden incluye ambos estados; para ordenar desde la interfaz se dejan los filtros vacíos.

Las escrituras verifican la versión del tema. La ordenación usa una revisión del directorio y un comando constante `directoryVersion`, `id`, `beforeId` (null mueve al final); desplaza únicamente el intervalo afectado. Un conflicto conserva la edición y permite recargar explícitamente. No se borra un tema ni se reutiliza su dirección.

Cada contenido puede asociar hasta doce temas distintos. La selección se guarda con la versión del contenido, conserva su formato, etiquetas y obra completa, y rota esa versión sólo si cambia el conjunto. El servidor serializa las asociaciones con la edición del contenido y con el archivo de los temas. Un tema archivado ya asociado puede mantenerse o retirarse explícitamente; no puede añadirse a otro contenido. El reintento de los metadatos no reemplaza la selección. Recargar asociaciones con cambios requiere confirmación.

Archivar conserva la identidad, las asociaciones y los contenidos. El directorio público muestra únicamente temas activos. El filtro opcional combina tema, formato y búsqueda literal; un tema desconocido o archivado con dirección válida devuelve una página vacía con HTTP 200. Una dirección mal formada devuelve 400. El filtro no cambia las reglas de publicación ni de acceso a la obra.

## Contratos HTTP

Administración (Content.Manage + MFA; no-store; CSRF/origen en escrituras):

- GET/POST `/api/v1/admin/catalog/topics`.
- GET/PUT `/api/v1/admin/catalog/topics/{id}`.
- PUT `/api/v1/admin/catalog/topics/{id}/state`.
- PUT `/api/v1/admin/catalog/topics/order`.
- GET `/api/v1/admin/catalog/topics/{id}/audit?page=1&pageSize=20`.
- GET/PUT `/api/v1/admin/content/{id}/topics`.

Público (metadatos; no-store):

- GET `/api/v1/catalog/topics?page=1&pageSize=20`.
- GET `/api/v1/catalog/content?topic=tema&category=lecturas&search=texto&page=1&pageSize=20`.

La auditoría de temas es de sólo lectura y paginada; registra actor, fecha y campos o posiciones modificados, sin copiar el nombre ni datos de la obra en el cambio. Las asociaciones se auditan atómicamente en la auditoría existente del contenido. Las lecturas paginadas usan Repeatable Read para obtener total y filas del mismo snapshot.

## Persistencia y recuperación

La migración aditiva `20261007040000_AddTopics` crea Topics, ContentTopics, TopicDirectory y TopicAudit en el schema Catalog, sus índices y relaciones Restrict. El singleton de revisión es infraestructura; el directorio inicia con cero temas. Un constraint trigger diferido impide superar doce asociaciones también mediante SQL. El runner conserva los roles separados y restringe el borrado de temas/revisión y la modificación o borrado de auditoría.

La migración y su snapshot se validan con `HasPendingModelChanges`, aplicación/idempotencia y PostgreSQL real antes del commit y del despliegue. El restore digest debe incluir estas cuatro tablas, índices, checks, relaciones y función/trigger; se combina con el incremento de consumo coordinado. No ejecutar una bajada automática de migraciones ni usar datos de producción en QA.

El documento funcional pide mantenimiento de categorías; esta implementación concreta la clasificación temática sin sustituir los seis formatos oficiales, sin introducir jerarquías obligatorias y sin alterar planes, facturación, SMTP ni YouTube.
