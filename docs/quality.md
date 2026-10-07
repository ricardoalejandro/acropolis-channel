# Calidad y verificación

La puerta de calidad es `bash scripts/verify.sh`, ejecutada en el checkout canónico del VPS. No usa GitHub Actions ni instala .NET o Node.js en el host. No despliega producción y no modifica DNS o Traefik.

## Ejecución y artefacto

Con `main` limpio y el cambio revisado:

```bash
cd /root/proyect/acropolis-channel
bash scripts/verify.sh
```

La ejecución fija el SHA inicial, construye una vez `acropolis-channel:<SHA>` y conserva su identificador. También construye `acropolis-channel-migrations:<SHA>`. Los argumentos y labels de construcción identifican el mismo SHA. Al terminar comprueba que el checkout y ambas referencias de imagen no cambiaron durante la verificación.

Para iterar antes de hacer commit:

```bash
bash scripts/verify.sh --working-tree
```

Ese modo usa etiquetas diferentes, produce `working_tree: true` y `deployment_eligible: false`, incluso si todos los pasos pasan. No certifica el contenido de un commit ni habilita su despliegue.

Cada ejecución guarda logs redactados, coberturas, auditorías, resultados Playwright, resumen k6 y un `report.json` en `.local/qa/<SHA>/<RUN>/`. El JSON incluye `sha`, `image_id`, `migration_image_id`, `status`, `deployment_eligible`, `passed_steps` y `path`. Un fallo produce un informe con `status: failed`; la suite nunca convierte una comprobación fallida en un éxito. `.local/` está ignorado y no es contenido público.

El despliegue puede reutilizar una imagen únicamente cuando existe un informe `passed`, apto para despliegue, cuyo SHA e identificadores coinciden con las imágenes presentes. La etiqueta por sí sola no demuestra que pasó QA. Consultar el informe real para conocer resultados; esta documentación describe el procedimiento y no afirma que una ejecución específica haya pasado.

## Aislamiento y límites

`compose.qa.yml` recibe valores propios de la ejecución y se invoca con `--env-file /dev/null`. Genera credenciales de QA nuevas; no carga `.env` de producción. Cada proyecto, base, red y volumen comienza con `acropolis_test_<RUN>`. No publica puertos, comparte volúmenes productivos, monta el socket Docker ni se conecta a la red de Traefik.

Hay tres ámbitos: el candidato, los tests de integración y la restauración del backup. Cada uno tiene su propio PostgreSQL 18 y volumen. Los tests de integración no inicializan ni modifican la base vacía con la que se verifica el primer despliegue de migraciones del candidato.

Los servidores de pruebas y el seed sintético usan Testing. El candidato HTTP usa Production para ejercitar la configuración desplegable, con conexión QA explícita y `AllowedHosts` limitado a los nombres internos utilizados por las pruebas. Esto no lo conecta a producción.

Los servicios limitan CPU, memoria y procesos; las construcciones .NET usan un único procesador/MSBuild sin paralelismo. Playwright tiene memoria compartida propia, no IPC del host. La limpieza, incluso después de un fallo, selecciona únicamente los nombres aleatorios creados por esa ejecución y retira sus contenedores, redes y volúmenes de pruebas. No purga Docker ni elimina datos de otros proyectos. Se conservan las imágenes candidatas y los informes para revisión y reutilización.

## Comprobaciones obligatorias

| Área | Comprobación |
| --- | --- |
| Scripts | Sintaxis Bash de scripts e inicialización PostgreSQL; sintaxis de helpers Python administrativos |
| Orquestación del despliegue | Tests Python con mocks y directorios temporales para SHA, estado Git, artefacto, DNS bloqueado y recuperación; no contactan Docker, Git, DNS ni producción |
| Imagen de ejecución | Usuario no root; sin fuente, `.env`, `.local`, metadata Git, node_modules, Node, Git ni SDK .NET; comprobación sin red y en modo read-only |
| Backend | Restore NuGet `--locked-mode`, build Release, formato y auditoría de dependencias transitivas; vulnerabilidades High/Critical bloquean |
| Unidad backend | Reglas reales de Platform, Identity, Catalog y Subscriptions: validación, estados, niveles, permisos y límites; cobertura por ensamblado de al menos 80% en líneas y ramas |
| HTTP y arquitectura | Tests del contrato HTTP, fallos sanitizados y dependencias permitidas |
| Integración | PostgreSQL real, configuración Testing obligatoria, base `acropolis_test_*` y credencial QA propia; sin fallback ni tests omitidos por falta de base; cobertura de líneas y ramas de Infrastructure de al menos 80%, excluyendo migraciones generadas y snapshot |
| Frontend | `npm ci`, typecheck, lint, formato, tests, cobertura mínima 80% en líneas, ramas, funciones y statements; build y auditoría npm High/Critical |
| Migraciones | Antes de migrar readiness 503 y liveness 200; primera migración desde base vacía; segunda ejecución sin cambios de schema o historial |
| Navegador | Playwright desktop/móvil contra frontend y API del candidato real; resultados y reporte conservados |
| Resiliencia | Al detener PostgreSQL readiness 503 y liveness 200; al recuperarlo readiness 200; reinicio de app y base con schema e historial persistentes |
| Recuperación | `pg_dump` custom, restauración en otro proyecto QA conservando ACL, comprobación de propietarios y permisos efectivos, readiness/saludo de la aplicación restaurada antes de reaplicar migraciones, reaplicación idempotente y comparación de schema e historial con el origen; intento de restaurar producción rechazado antes de contactar Docker |
| Carga | k6: referencia de salud/saludo con 20 VU y cinco iteraciones previas de calentamiento; identidad con 50 sesiones reales de cuentas sintéticas y 100.000 cuentas almacenadas; catálogo con 50 lectores y 10.000 fichas persistidas, incluidas 8.000 públicas, 1.000 borradores y 1.000 archivadas. Cada medición dura 60 segundos, con pausa de un segundo por iteración, p95 menor de 500 ms, tasa de errores HTTP 0 y todos los checks correctos |

La cobertura de Application mide decisiones de la capa sin infraestructura. Se excluyen exclusivamente Contracts.cs de Identity y Catalog, que declaran DTOs sin comportamiento; serialización y contratos se verifican mediante HTTP real. IdentityResult y sus decisiones permanecen en el alcance medido. Las métricas de líneas y ramas deben existir y ser válidas; un informe sin ramas reales se declara no aplicable para esa métrica. Los tests de PostgreSQL y del runner ejercitan SQL y permisos con una base real; no se contabilizan como unidad ni se reemplazan por mocks para aumentar el porcentaje.

La carga de identidad utiliza 100.000 cuentas sintéticas, sesiones obtenidas mediante acceso real, consultas de perfil/readiness y búsqueda administrativa. El setup respeta el límite de acceso por IP. El umbral describe una concurrencia acotada y no acredita 1.000 usuarios simultáneos ni reproducción multimedia. El resumen contiene mediciones observadas. El runner utiliza la [imagen oficial de k6](https://grafana.com/docs/k6/latest/set-up/install-k6/) con una versión fijada en Compose.

Readiness utiliza una fuente de conexiones Npgsql compartida por Identity, Catalog y Subscriptions y una consulta viva de los cuatro historiales EF. Los identificadores esperados se calculan desde las migraciones del binario; no se almacena en caché la disponibilidad de la base. PostgreSQL real comprueba historias faltantes, adicionales o desajustadas, tablas/permisos ausentes, bloqueo con plazo de dos segundos y recuperación posterior. La cancelación del cliente se distingue de una caída de la base.

`scripts/qa-diagnostics.py` guarda capturas privadas antes/después de las cargas y antes del cleanup: presión del host, límites/contadores de contenedores propios y conexiones agregadas. Conserva únicamente campos permitidos del evento `DatabaseReadinessFailure`, sin cuerpos, SQL de usuarios, excepciones completas, configuración o credenciales. Los logs son una muestra acotada y no prueban ausencia ni conteo total de errores; k6 conserva ese conteo. Un fallo del diagnóstico se registra como parcial y no cambia el resultado de la prueba ni evita limpiar recursos propios.

## Backups manuales y restauración de prueba

El respaldo manual usa la identidad ya configurada dentro del contenedor PostgreSQL. No muestra su contraseña ni requiere copiarla al shell. Escribe un archivo nuevo dentro de `.local/backups/` o `.local/qa/` y se niega a sobrescribirlo.

Ejemplo de respaldo de la base del proyecto, solamente cuando corresponde administrarla:

```bash
bash scripts/backup-db.sh \
  --project acropolis-channel \
  --database acropolis \
  --output "/root/proyect/acropolis-channel/.local/backups/manual-$(date -u +%Y%m%dT%H%M%SZ).dump"
```

`restore-db-test.sh` acepta exclusivamente un proyecto y una base `acropolis_test_*`, verifica la identidad Compose del contenedor y su `POSTGRES_DB`, y restaura en una transacción con salida de error obligatoria. Por ejemplo, con un destino QA ya creado mediante la configuración aislada:

```bash
bash scripts/restore-db-test.sh \
  --project acropolis_test_RUN_restore \
  --database acropolis_test_RUN_restore \
  --input /root/proyect/acropolis-channel/.local/backups/ARCHIVO.dump \
  --maintenance
```

Sustituir `RUN` por el identificador real en minúsculas y `ARCHIVO` por un backup existente. El destino requiere los roles de inicialización QA. El backup conserva las ACL y privilegios predeterminados propios; no incluye roles globales ni sus contraseñas. La restauración usa `--no-owner --role=acropolis_migrator`, por lo que los objetos quedan bajo el migrador del destino y las ACL originales se reponen contra los roles inicializados. El gate comprueba propiedad, permisos efectivos de la aplicación y readiness antes de ejecutar de nuevo el runner del mismo ámbito QA. El script no permite una restauración sobre el proyecto de producción.

El gate guarda el esquema inicial y el esquema e historial anteriores al backup en artefactos privados, y exige que el origen siga coincidiendo con la inicialización. PostgreSQL puede reescribir casts y paréntesis equivalentes de los CHECK al volver a interpretar un dump; por eso una comparación textual directa con el origen puede fallar aunque las restricciones permanezcan intactas.

La referencia para la comparación restaurada se obtiene del mismo backup mediante una restauración sólo del esquema, en una base nueva `${QA_DATABASE}_schema_reference` dentro del PostgreSQL QA original. Su nombre, longitud, ámbito y propietario se verifican; una base existente se rechaza. Se conserva la comparación completa de columnas, índices, restricciones y demás objetos, retirando únicamente los tokens aleatorios restrict/unrestrict y excluyendo propietario/ACL del texto porque se comprueban por separado. La referencia se elimina tras capturar su esquema, bajo comprobación de propietario; la limpieza del contenedor QA propio cubre también un fallo intermedio.

El esquema restaurado se compara con esa referencia canónica inmediatamente tras la restauración y nuevamente después del runner; la historia EF se compara por separado con el origen en ambos momentos, y el digest de fichas/auditoría y los permisos efectivos siguen siendo obligatorios. Los artefactos `schema-initial.sql`, `schema-source.sql`, `schema-reference.sql`, `schema-restored-before.sql`, `schema-restored.sql` y los historiales JSON anteriores y posteriores al runner permiten diagnosticar diferencias; un fallo conserva también el diff correspondiente. No se omiten CHECK ni se relajan restricciones para hacer pasar la recuperación.

## Identidad, HTTPS y prototipo

El candidato Production conserva claves persistentes incluso en QA. Se prueba tanto el correo habilitado como el modo explícito IDENTITY_EMAIL_ENABLED=false, sin registrar cuentas ni emitir mensajes en ese modo. La ejecución crea una CA exclusiva, certificado para el proxy HTTPS, certificado SMTP y protector PFX; los clientes confían únicamente en esa CA de prueba. La clave privada de la CA no se monta en el navegador. No utilizar ignoreHTTPSErrors, TLS inseguro ni certificados productivos.

Los recorridos de navegador cubren registro, correo en el buzón SMTP aislado, confirmación explícita, acceso, perfil, cambio/recuperación de contraseña, permisos y administración. Las pruebas backend con PostgreSQL ejercitan además concurrencia, tokens de un solo uso, bloqueo, ocho horas absolutas, revocación, auditoría y protección del último administrador. Verificar respuestas 401/403/409, CSRF y campos desconocidos; un botón oculto no es autorización.

Comprobar reintento SMTP, protección cifrada de payloads y sesiones persistentes después de reiniciar la imagen. Restaurar con web detenido, invalidar cuentas restauradas y demostrar que la cookie y credenciales antiguas no vuelven a admitir acceso. La revalidación individual exige confirmación y contraseña nuevas y no recupera automáticamente permisos anteriores.

El prototipo se construye como target separado y se valida con capturas de escritorio y móvil, navegación por teclado y axe. El build productivo no incluye las rutas o fixtures del catálogo de demostración. Conservar las capturas junto al informe; una auditoría automatizada no sustituye la revisión visual y funcional.

## Catálogo, segundo factor y correo diferido

Las cuatro historias de migración (platform, identity, catalog y subscriptions) deben coincidir con los manifiestos de las imágenes; se verifican inicialización, idempotencia, propietarios y permisos de los cuatro esquemas. El respaldo y la restauración deben preservar exactamente las fichas y auditorías editoriales, comprobadas mediante digest, además de sus fronteras públicas: borradores y archivados responden 404.

Catalog exige pruebas de categorías, límites, búsqueda literal sin controles, paginación estable, transiciones, slug inmutable tras publicar, concurrencia optimista, atomicidad de auditoría y separación de Content.Manage frente a Users.Manage y niveles institucionales. Verificar CSRF y campos desconocidos en POST/PUT. El navegador ejercita creación, publicación, conflicto sin perder texto, recarga explícita, archivo y filtros/paginación con Atrás/Adelante; la sinopsis se representa como texto, nunca HTML ejecutable ni multimedia restringida.

MFA utiliza el proveedor TOTP de ASP.NET Core Identity. Probar inscripción y acceso, obligatoriedad administrativa, ausencia de sesión completa antes del segundo factor, caducidad y consumo del desafío, bloqueo, repetición de TOTP, concurrencia de códigos de recuperación, reautenticación para cambios y revocación de sesiones. Tras restaurar una base antigua, también deben desaparecer secretos, desafíos, códigos de recuperación y pruebas MFA; la revalidación no recupera permisos anteriores. Los auxiliares QA que guardan semillas TOTP son privados y sólo aceptan el dominio/base de pruebas; nunca registrar secretos ni incorporar trazas con códigos de recuperación.

El modo sin correo exige respuestas 503/email_unavailable en las operaciones de emisión, ninguna cuenta nueva ni intento de envío, y acceso normal al catálogo y a cuentas ya confirmadas. La API pública capabilities sólo expone la disponibilidad booleana. Deben seguir siendo obligatorios el protector, las claves persistentes, HTTPS y los proxies exactos. Probar además configuración SMTP ausente o inválida que no se utiliza cuando el correo está deshabilitado.

La carga del catálogo mide listado y ficha con p95 inferior a 500 ms, cero fallos HTTP y todos los checks correctos. No acredita reproducción multimedia ni 1.000 usuarios simultáneos. Los recorridos de navegador se separan por ventanas para respetar el límite real por IP; no elevar los límites productivos para hacer pasar QA.


El servidor de correo tiene fuente, scripts, pruebas y gate independientes en `/root/proyect/naperu-mail/source`; sus reportes no certifican imágenes de Acrópolis. El gate de esta aplicación comprueba su guard de integración `scripts/smtp-network.py`: sólo inspección Docker, propietario/miembros/alias exactos, rechazo de DB/proxy/servicios ajenos y cambios durante la lectura. Las pruebas de preflight exigen nombre canónico/465/TLS implícito para SMTP propio habilitado y bloqueo seguro antes de efectos de despliegue. Configuración deshabilitada o externa usa --allow-empty sin relajar las otras restricciones. Toda metadata de estos tests es sintética; no inspeccionan redes productivas. SMTP/CA aislados siguen cubriendo TLS, autenticación, outbox, fallos y recorridos de registro/confirmación/recuperación.


## Mediateca, suscripción gratuita y propietario protegido

El gate conserva sus umbrales y flujos anteriores. Subscriptions añade pruebas de Application y PostgreSQL/HTTP con cobertura propia de al menos 80% de líneas y ramas. Sus historias, schema y privilegios se incluyen en readiness, inicialización, segunda ejecución y respaldo/restauración. El acceso a obras necesita identidad activa y confirmada, suscripción activa y publicación vigente; probar 401/403/404, no-store y ausencia de obra/YouTube en DTO públicos.

Probar activación concurrente/idempotente, una suscripción por cuenta, cancelación, reactivación y rechazo de autoservicio tras suspensión. La restauración suspende acceso activo restaurado, conserva auditoría anterior y registra su invalidación. No abrir automáticamente acceso después de revalidar la identidad.

El bootstrap de propietario usa una dirección exacta configurada sólo para CLI; QA usa exclusivamente una cuenta sintética. Verificar intento de bootstrap sobre cuenta pendiente/suspendida, idempotencia y concurrencia, autoridad protegida, delegación owner-only, separación de los tres permisos, conflictos de versión y cierre de sesiones tras cambios reales. Un guardado sin cambios no cierra sesiones. Restaurar elimina IsOwner, todos los permisos y MFA; el bootstrap inicial no sustituye recuperación. La recuperación exige nuevas credenciales y confirmación antes de reautorizar expresamente.

Para Catalog cubrir obra completa separada, límites de texto, YouTubeId validado, referencias ordenadas course→work/program→course, rechazo de duplicados/ciclos/borradores/referencias retiradas, slug estable, auditoría paginada y filtros de fechas/acción/objeto. El reproductor se carga al pulsar, conserva controles oficiales, origen acotado y errores de disponibilidad; ninguna navegación o recarga inicia reproducción. Una prueba determinista de player no acredita reproducción externa real; guardar por separado el resultado de una comprobación autorizada contra YouTube si la red permite realizarla.

Interfaz: revisar mediateca, cuenta, lectura/player, suscripción, backoffice y auditoría en escritorio/móvil. Teclado, foco, zoom, contenido largo, error, vacío, conflicto sin perder edición y cambios sin guardar requieren casos útiles. Fuentes locales/licencias y diferencias entre shell público/administrativo forman parte de revisión visual. Mantener los recorridos previos de registro, confirmación, login, recuperación y MFA.

La carga de 50 sesiones autentica cuentas sintéticas distintas, activa explícitamente una suscripción gratuita por cuenta y lee tanto su estado como una lectura completa publicada por el recorrido de QA. Estas lecturas privadas deben conservar no-store, cero errores y p95 inferior a 500 ms. El identificador y marcador sintético de la lectura se conservan en el artefacto privado modernization-consumption.json; no es una semilla de producción. La prueba externa de YouTube se ejecuta separadamente con QA_YOUTUBE_SMOKE=1 y exige reproducción y avance de tiempo reales; su ausencia no acredita al proveedor externo.
