# Arquitectura de Acropolis Channel

Acropolis Channel es un monolito modular: una API ASP.NET Core sirve el frontend React y compone los módulos. La base inicial implementa únicamente el saludo y las comprobaciones operativas. No contiene usuarios, contenidos, pagos ni otras entidades de negocio inventadas.

## Componentes presentes

| Componente | Responsabilidad | Dependencias permitidas |
| --- | --- | --- |
| `src/Acropolis.Api` | Composición, HTTP, errores, cabeceras, estáticos React y endpoints | Application e Infrastructure del módulo Platform |
| `src/Modules/Platform/Acropolis.Platform.Application` | Saludo y decisión de readiness, timeout y contratos | Bibliotecas base de .NET; sin EF, Npgsql, ASP.NET ni React |
| `src/Modules/Platform/Acropolis.Platform.Infrastructure` | PostgreSQL, EF Core, lectura del estado de migraciones y ejecución de migraciones | Application; EF Core y Npgsql |
| `src/Acropolis.Migrations` | Proceso explícito y limitado para aplicar las migraciones | Infrastructure; configuración por entorno |
| `frontend` | Interfaz React, llamada al saludo real y estados de carga, error y reintento | Contrato HTTP; no conoce PostgreSQL ni credenciales |

La API expone `GET /api/v1/greeting` con `{ "message": "Hola mundo" }`. React lo obtiene de la API y muestra un fallo cuando la petición no funciona. Una ruta desconocida bajo `/api/` devuelve 404; no recibe el HTML de la SPA. Frontend y API comparten origen, por lo que la base no requiere CORS abierto.

## Salud y persistencia

- `GET /health` verifica liveness y devuelve `200 { "status": "ok" }` sin abrir una conexión a PostgreSQL.
- `GET /health/ready` comprueba la conexión y que las migraciones aplicadas coinciden con las esperadas por el ensamblado. Devuelve 200/`ok` o 503/`not_ready`, con timeout. La respuesta no expone conexiones ni errores internos.
- El schema inicial es `platform`; su historial EF reside en `platform."__EFMigrationsHistory"`. El runner ejecuta la migración inicial real de ese schema e historial. No crea tablas de negocio ficticias.
- El arranque HTTP no aplica migraciones automáticamente. El runner tiene un límite temporal, un bloqueo advisory de sesión y salida de error sanitizada. Ejecutarlo de nuevo no cambia un estado que ya está actualizado.

PostgreSQL 18 conserva su directorio de datos en un volumen montado en `/var/lib/postgresql`. Hay tres identidades: `acropolis_admin` para inicialización y respaldo, `acropolis_migrator` propietario de esta base y su schema, y `acropolis_app` para el proceso HTTP sin DDL. La configuración se recibe por `ConnectionStrings__Database`; ninguna credencial se guarda en código o en el bundle React. La aplicación puede leer el historial de migraciones, pero no modificarlo.

## Construcción y ejecución

El Dockerfile usa Node.js 22 para React, el SDK .NET 10 para construir y ASP.NET Core .NET 10 para ejecutar. El host no necesita instalar esos runtimes. Los locks de NuGet y npm fijan las dependencias y se verifican antes de aceptar una imagen.

El target final `web` contiene `Acropolis.Api.dll` y los estáticos React; escucha HTTP en 8080 como usuario `app`. El target `migrations` contiene `Acropolis.Migrations.dll` y no es un servidor HTTP. Los targets `sdk`, `node` y `playwright` son herramientas de QA, no servicios de producción. Playwright conserva sus navegadores de la imagen oficial correspondiente y usa Node.js 22 del target `node`.

Producción mantiene la red interna de la aplicación y la red de publicación existente. Traefik termina HTTPS mediante el archivo dinámico propio del proyecto. QA utiliza proyectos, redes y volúmenes diferentes, sin puertos del host, socket Docker montado, red de Traefik ni `.env` de producción.

## Añadir módulos cuando exista una necesidad real

Un módulo nuevo tendrá un límite explícito dentro de `src/Modules/<Nombre>`. Su capa Application expresará casos de uso y contratos; Infrastructure implementará persistencia e integraciones. Una capa Domain se añadirá cuando existan reglas y entidades de negocio que la justifiquen. La API actuará como composición y adaptador HTTP.

Cada módulo será dueño de su persistencia y sus migraciones; no leerá ni escribirá directamente tablas internas de otro módulo. La interacción entre módulos usará contratos de Application y transacciones explícitas cuando sean necesarias. Un módulo no referenciará el host API. Los tests de arquitectura deben extenderse para verificar estas reglas al introducirlo.

Antes de añadir autenticación, publicación de contenidos u otro módulo, se debe definir su contrato, reglas, permisos, schema y escenarios de fallo. La estructura actual deja ese crecimiento posible sin presentar funcionalidades que aún no existen.
