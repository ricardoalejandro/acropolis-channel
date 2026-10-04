# Arquitectura de Acrópolis Channel

Monolito modular ASP.NET Core 10: un host sirve API y React del mismo origen y compone módulos con límites explícitos. PostgreSQL 18 almacena datos; el runner de migraciones es un proceso separado. La distribución multimedia futura en AWS no forma parte de esta fase.

## Componentes

| Componente | Responsabilidad |
| --- | --- |
| Acropolis.Api | Composición, HTTP, autenticación, autorización, antifalsificación, errores, cabeceras y estáticos |
| Platform.Application | Contratos del saludo y decisión de readiness |
| Platform.Infrastructure | Conexión, estado de migraciones y schema platform |
| Identity.Application | Contratos, validación y reglas de cuentas, niveles y permisos |
| Identity.Infrastructure | ASP.NET Identity, EF Core, sesiones, tokens, auditoría y outbox SMTP |
| Acropolis.Migrations | Migración explícita, inicialización administrativa y recuperación en mantenimiento |
| frontend | Aplicación React, formularios accesibles, perfil y administración |
| frontend/preview | Prototipo editorial separado del build productivo |

Application no depende de ASP.NET, EF, Npgsql ni del host. Infrastructure depende de su contrato Application. Ningún módulo referencia la API ni lee directamente las tablas internas de otro módulo. Las pruebas de arquitectura verifican estos límites.

## Identidad y autorización

ASP.NET Identity gestiona las credenciales y el bloqueo por intentos fallidos. El registro siempre crea una cuenta Externo sin permisos administrativos y exige confirmación de correo antes de permitir acceso. Las contraseñas tienen entre 15 y 128 caracteres, sin reglas arbitrarias de composición.

Los niveles Externo, Probacionista, Miembro, FFVV, Instructor y Hachado pueden coexistir. Son atributos institucionales, independientes de Users.Manage y de futuras suscripciones. La interfaz administrativa no puede conceder ese permiso; el primer administrador se inicializa mediante un comando explícito para una cuenta exacta y confirmada.

La cookie segura contiene la referencia protegida de una sesión persistida en PostgreSQL. Su duración máxima es ocho horas, sin renovación deslizante. El servidor comprueba estado y versión de seguridad de la cuenta en las peticiones autenticadas. Los cambios sensibles invalidan sesiones; no se confía en permisos antiguos del navegador.

Todas las mutaciones HTTP, incluidas las de acceso público, exigen antifalsificación. React obtiene el token del mismo origen y envía X-CSRF-TOKEN. Las cookies son Secure, HttpOnly y con prefijo __Host-. Sólo se aceptan cabeceras forwarded de las direcciones exactas del proxy configurado. API y UI no requieren CORS abierto.

Confirmación y recuperación usan tokens aleatorios de un solo uso: sólo se persiste su hash. La confirmación caduca en 24 horas y el reset en 30 minutos. El enlace transporta el token en un fragmento de URL que React retira; una petición GET no cambia el estado de la cuenta. Los errores de registro, reenvío y recuperación evitan revelar la existencia de cuentas.

El permiso administrativo se comprueba en servidor. Las actualizaciones usan versión de concurrencia, transacción, registro de auditoría y protección del último administrador. Las listas están paginadas y sus filtros no cargan la tabla completa en memoria.

## Correo y claves

El cambio de estado y la intención de correo se guardan juntos. El outbox cifra su payload con Data Protection y un worker envía por SMTP con TLS validado, reintentos y reclamaciones temporales recuperables. SMTP no participa en una transacción distribuida: puede existir un duplicado después de una aceptación remota seguida de un fallo local. El token sólo se consume una vez.

La aplicación usa un nombre estable para Data Protection, un key ring persistente y un certificado protector PFX privado. No se generan claves efímeras por reiniciar el contenedor. Se respaldan base, claves, protector y configuración juntos. Ninguno de estos materiales entra en Git ni en las imágenes.

La restauración de un backup antiguo exige invalidar sesiones, enlaces y correos restaurados, bloquear cuentas para revalidación y sustituir credenciales antiguas antes de reabrirlas. El procedimiento está en identity-operations.md. MFA queda pendiente antes del lanzamiento operativo.

## Persistencia y migraciones

Platform e Identity son propietarios de sus schemas e historiales de EF. El runner aplica Platform y después Identity bajo un único bloqueo advisory exclusivo; el host HTTP nunca aplica migraciones. La readiness comprueba ambos historiales esperados con timeout. Liveness no abre conexiones a PostgreSQL.

PostgreSQL conserva sus datos en /var/lib/postgresql. acropolis_admin se limita a inicialización y respaldo; acropolis_migrator es propietario de schemas; acropolis_app tiene permisos de ejecución sin DDL ni escritura de historiales. El runner configura privilegios de Identity también sobre una base ya existente, no sólo durante la inicialización de un volumen nuevo.

Las migraciones deben ser compatibles con la recuperación de imagen prevista. No ejecutar descensos automáticos ni compartir transacciones entre módulos sin un contrato y una necesidad explícitos.

## Construcción, ejecución y crecimiento

Docker construye React con Node 22 y backend con SDK 10. El runtime final sólo contiene ASP.NET Core, assemblies y estáticos productivos. Corre como app no root, con raíz de contenedor de sólo lectura y puertos internos. Los locks NuGet y npm fijan dependencias. Los assets con hash usan cache inmutable y JavaScript/CSS admiten Brotli/Gzip; el HTML exige revalidación. Las respuestas de identidad y administración usan no-store y quedan fuera de la compresión.

El target preview contiene el prototipo separado. Los targets sdk, node, playwright y qa-pki son herramientas de verificación y no se publican como la aplicación. El prototipo no añade endpoints de contenido falso, pagos ni autenticación simulada a producción.

QA crea CA, correo SMTP, bases y redes exclusivos de cada ejecución. El candidato usa configuración Production, TLS real y las mismas imágenes que se podrán desplegar. No se conectan los tests a Traefik, al socket Docker ni a datos productivos.

Los siguientes módulos se incorporarán cuando exista su contrato: catálogo, acceso a multimedia, suscripciones, pagos e integración institucional. Un nivel institucional no debe convertirse implícitamente en una suscripción ni en un rol administrativo. Cada proceso nuevo requiere reglas, límites, errores, idempotencia cuando corresponda y pruebas por nivel.
