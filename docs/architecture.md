# Arquitectura de Acrópolis Channel

Monolito modular ASP.NET Core 10: un host sirve API y React del mismo origen y compone módulos con límites explícitos. PostgreSQL 18 almacena datos; el runner de migraciones es un proceso separado. El piloto sirve lecturas completas y reproduce YouTube mediante su reproductor oficial. La distribución multimedia real en AWS sigue pendiente de datos e infraestructura concretos; su estado de implementación se describe en `modernization-scope.md`.

## Componentes

| Componente | Responsabilidad |
| --- | --- |
| Acropolis.Api | Composición, HTTP, autenticación, autorización, antifalsificación, errores, cabeceras y estáticos |
| Platform.Application | Contratos del saludo y decisión de readiness |
| Platform.Infrastructure | Conexión, estado de migraciones y schema platform |
| Identity.Application | Contratos, validación y reglas de cuentas, niveles y permisos |
| Identity.Infrastructure | ASP.NET Identity, EF Core, sesiones, tokens, auditoría y outbox SMTP |
| Catalog.Application | Contratos y reglas de edición, temas, observaciones de consumo y reportes |
| Catalog.Infrastructure | Catálogo y temas, transacciones de consumo, agregados diarios, reportes y retención |
| Subscriptions.Application | Contratos y reglas de acceso gratuito, estados e idempotencia |
| Subscriptions.Infrastructure | Persistencia propia, versiones, bloqueo por usuario, auditoría y reportes de eventos |
| Acropolis.Migrations | Migración explícita, inicialización administrativa y recuperación en mantenimiento |
| frontend | Aplicación React, formularios accesibles, perfil y administración |
| frontend/preview | Prototipo editorial separado del build productivo |

Application no depende de ASP.NET, EF, Npgsql ni del host. Infrastructure depende de su contrato Application. Ningún módulo referencia la API ni lee directamente las tablas internas de otro módulo. Las pruebas de arquitectura verifican estos límites.

## Identidad y autorización

ASP.NET Identity gestiona las credenciales y el bloqueo por intentos fallidos. El registro siempre crea una cuenta Externo sin permisos administrativos y exige confirmación de correo antes de permitir acceso. Las contraseñas tienen entre 15 y 128 caracteres, sin reglas arbitrarias de composición.

Los niveles Externo, Probacionista, Miembro, FFVV, Instructor y Hachado pueden coexistir. Son atributos institucionales independientes de los permisos y suscripciones. El propietario protegido se inicializa mediante un comando explícito auditado para una cuenta exacta configurada privadamente, confirmada y activa. Sólo el propietario delega Users.Manage, Content.Manage y Subscriptions.Manage; otros gestores conservan las tareas de sus permisos sin poder suspenderlo o quitarle autoridad. No se altera contraseña, confirmación o MFA por bootstrap.

La cookie segura contiene la referencia protegida de una sesión persistida en PostgreSQL. Su duración máxima es ocho horas, sin renovación deslizante. El servidor comprueba estado y versión de seguridad de la cuenta en las peticiones autenticadas. Los cambios sensibles invalidan sesiones; no se confía en permisos antiguos del navegador.

Todas las mutaciones HTTP, incluidas las de acceso público, exigen antifalsificación. React obtiene el token del mismo origen y envía X-CSRF-TOKEN. Las cookies son Secure, HttpOnly y con prefijo __Host-. Sólo se aceptan cabeceras forwarded de las direcciones exactas del proxy configurado. API y UI no requieren CORS abierto.

Confirmación y recuperación usan tokens aleatorios de un solo uso: sólo se persiste su hash. La confirmación caduca en 24 horas y el reset en 30 minutos. El enlace transporta el token en un fragmento de URL que React retira; una petición GET no cambia el estado de la cuenta. Los errores de registro, reenvío y recuperación evitan revelar la existencia de cuentas.

El permiso administrativo se comprueba en servidor. Las actualizaciones usan versión de concurrencia, transacción, registro de auditoría y protección del último administrador. Las listas están paginadas y sus filtros no cargan la tabla completa en memoria.

## Correo y claves

El cambio de estado y la intención de correo se guardan juntos. El outbox cifra su payload con Data Protection y un worker envía por SMTP con TLS validado, reintentos y reclamaciones temporales recuperables. SMTP no participa en una transacción distribuida: puede existir un duplicado después de una aceptación remota seguida de un fallo local. El token sólo se consume una vez.

La aplicación usa un nombre estable para Data Protection, un key ring persistente y un certificado protector PFX privado. No se generan claves efímeras por reiniciar el contenedor. Se respaldan base, claves, protector y configuración juntos. Ninguno de estos materiales entra en Git ni en las imágenes.

La restauración de un backup antiguo exige invalidar sesiones, enlaces y correos restaurados, bloquear cuentas para revalidación y sustituir credenciales antiguas antes de reabrirlas. El procedimiento está en identity-operations.md. MFA usa el proveedor oficial de códigos de autenticador de ASP.NET Identity. Una contraseña válida inicia un desafío limitado cuando se necesita segundo factor; no emite una sesión completa antes de verificarlo. Las políticas Users.Manage, Content.Manage y Subscriptions.Manage requieren además la prueba amr=mfa. Claves cifradas, códigos de recuperación de un solo uso, control transaccional de reutilización y recuperación se describen en mfa-operations.md.

## Persistencia y migraciones

Platform, Identity, Catalog y Subscriptions son propietarios de sus schemas e historiales de EF. El runner aplica Platform, Identity y después Catalog y Subscriptions bajo un único bloqueo advisory exclusivo; el host HTTP nunca aplica migraciones. La readiness comprueba los cuatro historiales esperados con timeout. Liveness no abre conexiones a PostgreSQL.

PostgreSQL conserva sus datos en /var/lib/postgresql. acropolis_admin se limita a inicialización y respaldo; acropolis_migrator es propietario de schemas; acropolis_app tiene permisos de ejecución sin DDL ni escritura de historiales. El runner configura privilegios de Identity también sobre una base ya existente, no sólo durante la inicialización de un volumen nuevo.

La búsqueda administrativa conserva coincidencias por fragmento de correo o nombre, sin distinguir mayúsculas; %, _ y barra invertida son caracteres literales. PostgreSQL utiliza índices GIN de pg_trgm, separados del índice único del correo normalizado. El migrador instala esta extensión trusted únicamente en el schema identity de la base del proyecto; el runtime no crea extensiones ni índices.

Las migraciones deben ser compatibles con la recuperación de imagen prevista. No ejecutar descensos automáticos ni compartir transacciones entre módulos sin un contrato y una necesidad explícitos.

## Construcción, ejecución y crecimiento

Docker construye React con Node 22 y backend con SDK 10. El runtime final sólo contiene ASP.NET Core, assemblies y estáticos productivos. Corre como app no root, con raíz de contenedor de sólo lectura y puertos internos. Los locks NuGet y npm fijan dependencias. Los assets con hash usan cache inmutable y JavaScript/CSS admiten Brotli/Gzip; el HTML exige revalidación. Las respuestas de identidad y administración usan no-store y quedan fuera de la compresión.

El target preview contiene el prototipo separado. Los targets sdk, node, playwright y qa-pki son herramientas de verificación y no se publican como la aplicación. El prototipo no añade endpoints de contenido falso, pagos ni autenticación simulada a producción.

QA crea CA, correo SMTP, bases y redes exclusivos de cada ejecución. El candidato usa configuración Production, TLS real y las mismas imágenes que se podrán desplegar. No se conectan los tests a Traefik, al socket Docker ni a datos productivos.

Sólo los pagos están aplazados por indicación del propietario. La integración institucional y la facturación electrónica están solicitadas y requieren identificar sus contratos reales; consultar `modernization-scope.md`. El consumo actual coordina contratos públicos de Identity, Subscriptions y Catalog en el host, sin consultar tablas privadas entre módulos. Un nivel institucional no debe convertirse implícitamente en una suscripción ni en un rol administrativo. Cada proceso nuevo requiere reglas, límites, errores, idempotencia cuando corresponda y pruebas por nivel.


## Catálogo editorial y disponibilidad del correo

Catalog separa fichas/sinopsis públicas de WorkText y referencias YouTube para consumo. La API pública filtra por published; draft y archived devuelven 404. Sus DTO públicos no incluyen obra completa, referencia multimedia privada o claves de objetos AWS. Cursos ordenan obras y programas ordenan cursos; las referencias se validan al publicar y al consumir, con profundidad acotada. Las seis categorías y los assets de cubierta permitidos son enumeraciones explícitas; no se aceptan URLs arbitrarias. La búsqueda literal por título/resumen usa índices GIN y la extensión pg_trgm existente. Los listados tienen orden determinista y paginación en SQL. Las respuestas no se almacenan en caché para que la retirada de una publicación surta efecto en nuevas consultas.

Content.Manage se concede y revoca por el propietario mediante una API estrecha, con versión y auditoría; la CLI anterior permanece compatible para operaciones autorizadas y no puede cambiar al propietario; es independiente de Users.Manage y de los niveles institucionales. Cada cambio editorial usa versión de concurrencia y auditoría en la misma transacción de persistencia. El slug queda estable después de la primera publicación. Se archiva sin DELETE físico y el rol runtime no puede borrar contenidos ni reescribir auditoría.

Temas son una clasificación adicional, sin sustituir formatos ni tags. El backoffice exige Content.Manage y MFA y permite CRUD sin borrado físico, orden, archivo/restauración y asociación a obras. El directorio tiene una versión de concurrencia propia; las asignaciones usan la versión de obra y guardan auditoría atómica. Los bloqueos se adquieren en orden estable y el límite técnico de doce asociaciones tiene una guarda diferida en PostgreSQL. Archivar conserva asociaciones; el filtro público sólo consulta temas activos y obras publicadas. La instalación comienza sin taxonomía precargada.

Identity.EmailEnabled=true sigue siendo el valor por defecto de código para conservar el comportamiento existente. El ejemplo de configuración declara false para preparar una instalación sin SMTP validado; el modo vigente se consulta en capabilities y el manifiesto activo, pues la integración real ya puede estar habilitada. Ese modo elimina nuevas operaciones HTTP de emisión de correo y detiene el dispatcher/worker; no modifica confirmaciones, contraseñas, MFA, claves persistentes ni sesiones existentes. Al habilitarlo se vuelven a exigir credenciales SMTP y TLS; los mensajes anteriores sólo se procesan si su flujo continúa vigente. /identity/capabilities expone únicamente el booleano necesario para la interfaz.


## Suscripción y consumo

El WIP actual mantiene como máximo una suscripción free_beta por usuario, sin caducidad. Este piloto no sustituye los tres tipos, fechas y renovaciones del documento funcional: esas reglas están pendientes de confirmación del propietario, conforme a `modernization-scope.md`. La activación es explícita, idempotente y disponible para una cuenta confirmada y activa. El usuario puede cancelar o volver a activar una cancelación; una suspensión administrativa no se elimina por autoservicio. Cambios administrativos exigen Subscriptions.Manage, MFA y versión, con auditoría transaccional. Un gestor ajeno no puede cambiar el acceso del propietario.

La API de consumo vuelve a comprobar identidad activa/confirmada, suscripción activa y obra publicada; devuelve no-store y no introduce material restringido en metadata pública. Cursos y programas sólo entregan referencias publicadas y su orden. Los controles del reproductor YouTube son oficiales, cargados al pulsar reproducir; referencias de origen se limitan a ese iframe/script, conservando no-referrer global en cuentas y tokens. Los vídeos públicos de YouTube continúan accesibles en su origen; esta autorización del portal no demuestra exclusividad del recurso.

Subscriptions agrega eventos exclusivamente desde su auditoría, por transición y día UTC. El intervalo es de una a 366 fechas, con extremo final exclusivo. Los eventos no clasificables siguen visibles y los días sin eventos se incluyen. El contrato `recorded_events` no representa cuentas únicas, ingresos ni estado histórico diario. Exige Subscriptions.Manage y MFA y no expone identificadores de usuario. Esta consulta no cambia la política de conservación de auditoría.

Restaurar no recupera IsOwner, permisos ni MFA. Las suscripciones activas restauradas se suspenden en mantenimiento antes de abrir acceso. La recuperación del propietario requiere revalidación de credenciales/correo y una operación explícita posterior; bootstrap inicial no evade esa recuperación.

## Observaciones de consumo y conservación

El código preparado concentra la persistencia de observaciones en Catalog; el host coordina contratos públicos de Identity y Subscriptions para comprobar cuenta activa confirmada y suscripción activa. El catálogo vuelve a comprobar publicación y versión de obra. Consultar la obra por GET no registra actividad. La interfaz observa lecturas visibles y eventos del reproductor YouTube oficial; son muestras aceptadas por el servidor, no prueba de atención humana.

`Catalog:Consumption:RecordingEnabled` vale false por defecto. La grabación requiere una activación explícita; la aprobación de retención no la habilita en producción. Cada visita y versión de obra inicia una sesión, vinculada a la cuenta y al hash de su versión de seguridad. La sesión y recibos técnicos caducan a las ocho horas. Una secuencia y cuerpo iguales devuelven el recibo previo; un cuerpo distinto o secuencia incorrecta produce conflicto. El servidor limita el crédito temporal, cuerpo de ocho KiB y frecuencia de peticiones.

Sesión o recibo y los dos agregados se escriben en una única transacción: `ConsumptionAccountDaily` conserva cuenta/obra/versión/fecha durante 90 fechas UTC, incluida la actual; `ConsumptionDaily` conserva generales por obra/versión/fecha durante 365 fechas UTC, sin AccountId. El worker de retención ejecuta lotes de hasta 1000 filas por tabla, diez iteraciones y 30 segundos por ciclo, con pausa de un minuto. Elimina recibos antes de sesiones para acotar cascadas, y no reconstruye agregados generales desde detalle ya vencido. Funciona independientemente de la grabación; en Testing requiere opt-in antes de crear scopes o acceder a DB.

Los reportes consultan SQL bajo transacción readonly RepeatableRead y paginan en servidor. General exige Content.Manage+MFA; detalle por cuenta exige además Users.Manage. Todas las respuestas son no-store. Tiempo de sesiones puede solaparse; progreso medio usa numerador y número de muestras conocidas y conserva desconocidos como tales. Cuentas distintas son exactas sólo para el detalle disponible de 90 fechas: intervalos anteriores devuelven null y fecha de disponibilidad. No sumar únicos diarios ni reconstruir únicos anuales desde datos vencidos. La política se limita a consumo y no reduce auditorías de otros procesos.

Las migraciones, ACL, transacciones, replay y contratos tienen pruebas preparadas. Esa preparación no acredita compilación, paridad EF/PostgreSQL, ejecución de tests o imágenes certificadas: los resultados corresponden al gate del commit definitivo.

## Modalidad futura de medios propios

La fase actual conserva YouTube como proveedor de reproducción. La evolución con S3 privado, CloudFront y procesamiento de medios, sus límites de autorización y las pruebas necesarias están en [Modalidad AWS futura](aws-media-roadmap.md). Este diseño no habilita recursos ni acredita distribución AWS.
