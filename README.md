# acropolis-channel

Repositorio: [ricardoalejandro/acropolis-channel](https://github.com/ricardoalejandro/acropolis-channel).

El repositorio contiene la automatización inicial de despliegue. Todavía no incluye una aplicación, un Dockerfile ni un archivo Compose.

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

Añadir la aplicación, su `Dockerfile` y un archivo `compose.yml` en la raíz; configurar las variables de ejecución en el servidor y preparar su publicación siguiendo la documentación local. Comprobar el primer despliegue antes de activar la automatización.

Mantener el workflow y el script dedicados al despliegue. Preservar los datos, volúmenes y configuraciones existentes al modificar el proceso.
