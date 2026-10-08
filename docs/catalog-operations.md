# Catálogo y obras

La estructura conserva las seis categorías oficiales y el documento funcional. Las reglas confirmadas distinguen Gratuito limitado a obras marcadas gratuitas y planes temporales de catálogo completo; consultar docs/subscriptions-operations.md y docs/modernization-scope.md. No sembrar datos de demostración en producción ni confundir el prototipo con contenido disponible.

## Ficha y obra completa

Body es siempre una sinopsis pública de texto plano (máximo 50.000 caracteres), Summary un resumen de hasta 600. WorkText es otro campo para la lectura completa; no insertar material protegido en Body. Author y Tags son metadatos opcionales acotados. Las cubiertas siguen limitadas a assets permitidos, con alternativa sin imagen; duración es opcional (segundos, 1 a 86.400).

El campo interno YouTubeId acepta exclusivamente identificadores válidos de once caracteres, nunca una URL arbitraria, HTML de embed o credencial. Los formatos audiovisuales usan el reproductor oficial de YouTube y sus controles; Podcast alojado allí usa ese mismo reproductor completo. No extraer audio, descargar el vídeo ni ocultar controles. La reproducción sólo se inicia por elección del visitante. Si no está disponible o no admite embed, informar y ofrecer su enlace de origen. El piloto no garantiza exclusividad de recursos públicos.

Una ficha antigua sin obra puede conservarse publicada; no afirmar que ya permite reproducción. Para ofrecer lectura completa o reproducción, el editor debe adjuntar el material autorizado del formato correspondiente. La API también permite publicar fichas sólo con sinopsis; en ese caso se muestran metadatos y no se promete acceso a una obra ausente. Los DTO públicos nunca contienen WorkText o YouTubeId. /api/v1/consumption/content/{slug} exige identidad activa confirmada, acceso vigente y publicación vigente, sin caché compartida. `IsFree` es metadato público y editorial: false por defecto, sin liberar obras existentes. Sólo una obra marcada true puede consumirse con Gratuito. Cambiar la marca actualiza versión y auditoría atómicamente; lectura, inicio, pulso y reintento vuelven a verificarla junto con el acceso. El administrador elige explícitamente la marca; no se deduce de la categoría ni del proveedor YouTube.

## Añadir un vídeo

1. En **Contenidos**, crea o abre una ficha. En **Categoría del contenido**, selecciona **Videos**, **Documentales**, **Podcast** o **Charlas online**, según corresponda, para que aparezca el campo de YouTube. Completa el título y la sinopsis.
2. En **Enlace o identificador de YouTube**, pega la dirección HTTPS del vídeo. Se admiten el enlace habitual de YouTube, el enlace corto de youtu.be, Shorts, directos y enlaces de inserción. También puedes introducir su identificador de once caracteres. Pega la dirección, no el código HTML de inserción.
3. Para una ficha nueva, elige **Guardar borrador**; al editar una existente, **Guardar cambios**. Cuando esté lista para publicarse, elige **Publicar contenido** y confirma la acción.

La aplicación conserva sólo el identificador del vídeo. Que el enlace sea válido no garantiza su reproducción: la disponibilidad y el permiso para verlo dentro de Acrópolis Channel dependen de YouTube. Si la reproducción no está disponible, se ofrece el enlace al vídeo de origen.

El alojamiento en AWS será una modalidad futura. Esta preparación no crea recursos ni configura ese servicio.

## Cursos y programas

La categoría Cursos admite CollectionKind course o program y ItemIds ordenados. Un curso referencia obras publicadas utilizables; un programa referencia cursos publicados utilizables. No hay ciclos, referencias duplicadas, grupos dentro de cursos ni programas dentro de programas. Se verifica la disponibilidad de sus descendientes al publicar y al consumir; una retirada impide entregar ese recorrido. Marcar gratuito un curso o programa no libera sus elementos: cada obra comprueba su propia marca y acceso. No se incluyen exámenes, diplomas o avance pedagógico avanzado en esta entrega. Programas no se convierte en séptima categoría principal.

## Edición y auditoría

Crear en /admin/content genera un borrador. Estados draft→published, published→draft/archived y archived→draft; se puede editar sin cambiar estado. La primera publicación fija slug y fecha. No existe DELETE físico desde la aplicación.

Cada modificación exige la versión devuelta por servidor; 409 conserva el texto para decidir si recargar. Publicar, retirar o archivar exige intención clara. No confiar en botones ocultos: Content.Manage y MFA se comprueban en API, de forma independiente a niveles y Users.Manage. Sólo el propietario delega permisos por la API estrecha; la CLI editorial existente se conserva para operaciones administrativas autorizadas y no permite quitar autoridad al propietario.

Auditoría atómica inmutable, de sólo lectura y paginada, por objeto o módulo con filtros de fecha/acción/objeto. No guardar la obra completa ni claves en Changes. El schema catalog conserva Contents, Audit y su historia EF; migraciones sólo runner bajo bloqueo. Runtime sin DDL, DELETE de obras ni UPDATE/DELETE de auditoría.

## QA y continuidad

qa-seed-catalog --count 10000 exige Testing y base acropolis_test_*. Mantiene 8.000 públicos, 1.000 borradores y 1.000 archivados en seis categorías; repetir no sobrescribe datos. Nunca ejecutar semillas en producción. La carga acotada mide catálogo, no streaming ni mil usuarios simultáneos.

Verificar límites, autorización, ausencia de filtración pública, referencias/orden, conflictos, retirada y auditoría, componentes y recorridos escritorio/móvil. Restaurar conserva exactamente obras/auditoría; la invalidación de identidad/suscripción posterior impide reabrir acceso revocado. AWS permanece futuro; pagos, facturación y gestión comercial pasan a fases posteriores por indicación del propietario. Conservar sus requisitos e identificar el mecanismo existente cuando se retomen; no simular integraciones, comprobantes o tarifas del sitio anterior.
