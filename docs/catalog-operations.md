# Catálogo editorial

La estructura se deriva de las capturas oficiales y del documento Proyecto Acrópolis Channel v1.0.0 (borrador, 09/09/2026). El documento no define la matriz de acceso por obra, plan y nivel. Esta entrega publica únicamente fichas y sinopsis editoriales: nunca lecturas completas, archivos privados ni URLs de reproducción.

## Trabajo editorial

Categorías: Lecturas, Documentales, Videos, Podcast, Charlas online y Cursos. La portada y /explore consultan datos reales, con estados de carga, error y ausencia de resultados; no se incorporan datos de demostración al catálogo productivo. /content/{slug} muestra sólo contenido publicado.

Crear contenido en /admin/content genera un borrador. Para publicarlo deben existir título, slug, resumen y sinopsis. Las cubiertas se limitan a los assets editoriales aprobados del proyecto, con opción sin imagen. Duración es opcional y se expresa en segundos (1 a 86.400). El texto se muestra escapado; no se interpreta HTML. La sinopsis tiene un máximo de 50.000 caracteres y el resumen de 600.

Estados: draft→published; published→draft o archived; archived→draft. Es posible editar dentro del mismo estado. Para volver a publicar un archivado debe pasar por borrador. La primera fecha de publicación y el slug se conservan después de retirar o archivar. No hay borrado físico desde la aplicación.

Cada actualización requiere la versión recibida del servidor. Un conflicto devuelve 409 y la interfaz conserva lo escrito, permitiendo decidir si se recarga. Publicar, retirar y archivar requieren una confirmación concreta en la interfaz. La API pública nunca devuelve borradores/archivados ni metadatos internos de concurrencia. Sus respuestas no-store permiten reflejar la retirada sin caché persistente.

## Acceso editorial

El nivel institucional y Users.Manage no conceden Content.Manage. Para una cuenta exacta ya registrada, confirmada y activa, un operador autorizado puede usar la imagen de migraciones validada:

```bash
docker compose -p acropolis-channel --profile migration run --rm migrations grant-content-manager --email CORREO_EXACTO
docker compose -p acropolis-channel --profile migration run --rm migrations revoke-content-manager --email CORREO_EXACTO
```

Estos comandos cambian permisos y revocan sesiones; ejecutarlos sólo para cuentas cuya autoridad se haya comprobado. El acceso editorial exige MFA. Con SMTP pospuesto no se crean cuentas confirmadas por atajo. La revalidación de una cuenta restaurada retira Content.Manage; una concesión posterior sigue siendo una decisión explícita.

## Datos y verificación

El schema catalog conserva Contents, Audit y su historial EF. Las migraciones se aplican después de Platform e Identity bajo el mismo bloqueo exclusivo. El runtime no tiene DDL, DELETE de contenidos ni UPDATE/DELETE de auditoría. El respaldo conserva contenido, estados, versiones e historial; QA compara también un digest completo de contenidos y auditoría después de restaurar.

qa-seed-catalog --count 10000 sólo admite entorno Testing y base acropolis_test_*. Crea 8.000 publicados, 1.000 borradores y 1.000 archivados en seis categorías, con nombres inequívocos de QA; repetirlo no sobrescribe datos existentes. Nunca ejecutar semillas en producción. La carga de QA es acotada y no certifica capacidad multimedia o comercial.

## Dependencias de las siguientes entregas

Para reproducción y acceso a obras se necesita definir matriz de permisos por plan/nivel/contenido, material autorizado y la entrega privada en AWS. Cursos/programas deberán agrupar obras ordenadas una vez definido su contrato. No convertir el campo de sinopsis en almacén de material protegido.

El documento contempla gratuito, Probacionismo de tres meses y anual; pagos con tarjeta (únicos/recurrentes), transferencia, Yape y efectivo validado, junto con facturación. Faltan proveedor, precios y moneda definitivos, reglas de renovación, validación institucional y contrato de facturación. Los precios del sitio antiguo son sólo referencia. Implementar esos cobros exige estas decisiones, idempotencia, conciliación y pruebas de fallos; no simular una integración ni crear recursos cloud como parte del catálogo.
