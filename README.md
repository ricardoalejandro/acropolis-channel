# acropolis-channel

Repositorio: [ricardoalejandro/acropolis-channel](https://github.com/ricardoalejandro/acropolis-channel). Rama de trabajo: `main`.

Aplicación inicial de Acropolis Channel: una página de presentación en español y un servidor HTTP Node.js sin dependencias. La página está en `public/index.html`, el servidor en `server.mjs` y `/health` devuelve el estado del servicio.

## Trabajo directamente en el VPS

El código fuente se modifica en el checkout del VPS. La carpeta canónica, el acceso administrativo y los detalles de infraestructura están documentados en `.local/vps-deployment.md` del servidor. Esa documentación es privada, está excluida de Git y no se descarga al clonar el repositorio.

Conectarse mediante la identidad administrativa existente y entrar en la carpeta indicada en esa documentación. Antes de editar:

```bash
git status --short --branch
git remote -v
git fetch origin
```

Confirmar `main` y el repositorio correcto. Sincronizar con `git pull --ff-only origin main` cuando el estado del checkout lo permita. Conservar cambios locales, archivos privados y commits anteriores; resolver cualquier divergencia sin descartar trabajo. Revisar y publicar los cambios del código en `main` desde el VPS.

GitHub Actions está desactivado y los workflows y credenciales dedicados a su despliegue se retiraron. Se conserva el historial de ejecuciones existente. Las operaciones de despliegue se realizan directamente desde el VPS.

## Entorno de desarrollo y preparación

Para desarrollo en un entorno con Node.js 22 o superior, ejecutar `npm start` y abrir `http://localhost:3000`. No hay dependencias npm adicionales.

El `Dockerfile` proporciona Node.js 22 y ejecuta la aplicación como un usuario sin privilegios. En el VPS se utiliza ese entorno Docker; no es necesario cambiar la versión de Node.js compartida del host.

Definir `TRAEFIK_NETWORK` en el archivo privado `.env` del VPS con el nombre de la red externa de publicación existente. Consultar `.local/vps-deployment.md` para el valor concreto. Compose conecta el servicio `web` a esa red con el alias `acropolis-channel-web`, sin publicar un puerto del host directamente.

Preparar y comprobar la imagen:

```bash
docker compose -p acropolis-channel config --quiet
docker compose -p acropolis-channel build web
```

Los archivos `.env` y la carpeta `.local/` están excluidos de Git y del contexto de construcción Docker. No incluir claves, tokens, contraseñas ni detalles administrativos privados en archivos versionados o imágenes.

## Despliegue manual

Ejecutar únicamente cuando la tarea requiera desplegar, desde el checkout del VPS:

```bash
bash scripts/deploy.sh
```

El script actualiza `main` con `git pull --ff-only origin main` y ejecuta `docker compose -p acropolis-channel up -d --build`. Conservar el nombre de proyecto Compose `acropolis-channel`. Revisar y publicar primero cualquier cambio pendiente que afecte al despliegue.

El dominio, routing y HTTPS deben configurarse en la infraestructura de publicación existente antes de considerar accesible la aplicación. Consultar la documentación privada y comprobar el estado real antes de modificar esa configuración.

Después de un despliegue solicitado:

```bash
docker compose -p acropolis-channel ps
docker compose -p acropolis-channel logs --tail=100
```

Comprobar también `/health` y la URL publicada. Una construcción correcta no demuestra que la aplicación esté desplegada o disponible públicamente.

## Reglas de operación

Preservar acceso administrativo, autenticación GitHub, datos, volúmenes, archivos privados, configuración de publicación y servicios de otros proyectos. No usar actualizaciones destructivas de Git, purgas Docker ni eliminación de volúmenes para preparar o desplegar este proyecto.

Mantener estas instrucciones y `.local/vps-deployment.md` actualizadas cuando cambien el proceso o la infraestructura. La preparación del 4 de octubre de 2026 deja el código y la imagen disponibles; el arranque y la publicación se realizarán en una tarea posterior.
