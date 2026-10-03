# acropolis-channel

Repositorio: [ricardoalejandro/acropolis-channel](https://github.com/ricardoalejandro/acropolis-channel).

Aplicación inicial de Acropolis Channel con una página de presentación, un servidor Node.js sin dependencias y despliegue con Docker Compose.

## Desarrollo local

Con Node.js 22 o superior, ejecutar `npm start` y abrir `http://localhost:3000`. La página se encuentra en `public/index.html` y el servidor en `server.mjs`. El endpoint `/health` permite comprobar manualmente si la aplicación responde.

## Despliegue con GitHub Actions

El workflow [Deploy to VPS](.github/workflows/deploy.yml) se ejecuta al recibir un push a `main` o mediante ejecución manual. Solo despliega cuando la variable `DEPLOY_ENABLED` tiene el valor literal `true`. Mantenerla en `false` hasta que la aplicación y su configuración Compose estén preparadas.

La configuración del entorno se proporciona mediante GitHub Actions:

| Nombre | Configuración |
| --- | --- |
| `VPS_HOST` | Variable: host de destino |
| `VPS_PORT` | Variable: puerto SSH |
| `VPS_USER` | Variable: usuario SSH |
| `DEPLOY_ENABLED` | Variable: habilita o deshabilita el despliegue |
| `VPS_SSH_PRIVATE_KEY` | Secret: clave SSH dedicada al despliegue |
| `VPS_SSH_KNOWN_HOSTS` | Secret: identidad verificada del servidor SSH |

El workflow conecta por SSH y solicita el comando de despliegue autorizado. El script [scripts/deploy.sh](scripts/deploy.sh) actualiza `main` con `git pull --ff-only` y ejecuta Docker Compose en el servidor. Los despliegues se realizan de forma secuencial y la construcción de contenedores ocurre en el VPS.

## Información local del VPS

Los valores concretos, instrucciones administrativas y detalles de infraestructura se conservan exclusivamente en `.local/vps-deployment.md` dentro del contenedor de trabajo. La carpeta `.local/` está excluida mediante `.gitignore` y no se obtiene al clonar el repositorio.

Para futuras modificaciones de Actions, consultar ese archivo local y configurar los valores en Variables o Secrets según corresponda. No copiar datos privados al workflow, al README ni a otros archivos versionados.

## Preparación de la aplicación

El `Dockerfile` ejecuta la aplicación como un usuario sin privilegios. `compose.yml` conecta el servicio web a la red externa de publicación existente, sin exponer un puerto del servidor directamente. Definir `TRAEFIK_NETWORK` en el archivo `.env` del servidor con el nombre de esa red. Configurar el routing y HTTPS siguiendo la documentación local del VPS, comprobar el primer despliegue y activar después la automatización.

La carpeta `.local/` también está excluida del contexto de construcción mediante `.dockerignore`, junto con los archivos `.env`, para mantener los datos privados fuera de las imágenes.

Mantener el workflow y el script dedicados al despliegue. Preservar los datos, volúmenes y configuraciones existentes al modificar el proceso.
