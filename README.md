# acropolis-channel

Repositorio: [ricardoalejandro/acropolis-channel](https://github.com/ricardoalejandro/acropolis-channel). Rama de despliegue: **main**. Directorio de trabajo: **/root/proyect/acropolis-channel**.

## Estado inicial — 3 de octubre de 2026

La automatización de despliegue está configurada y desactivada con **DEPLOY_ENABLED=false**. Todavía no hay aplicación, Dockerfile, archivo Compose, dominio ni proyecto Acropolis registrado en el panel de Dokploy.

La carpeta del VPS contiene una copia superficial del repositorio. No se instalaron herramientas adicionales. Se comprobó la autenticación de la clave SSH dedicada sin abrir una sesión remota ni ejecutar el despliegue. Una ejecución omitida de Actions no demuestra que una aplicación haya sido desplegada.

## VPS y acceso administrativo

| Dato | Valor |
| --- | --- |
| Dirección del VPS | 72.61.37.46 |
| Puerto SSH | 22 |
| Usuario SSH | root |
| Hostname | srv1095953 |
| Alias SSH en la máquina utilizada para configurar | vps |
| Directorio del proyecto | /root/proyect/acropolis-channel |
| Panel Dokploy | Puerto 3000 |
| Docker Compose instalado al configurar | v5.0.0 |
| Red de publicación existente | dokploy-network; overlay, externa y attachable |
| Traefik existente | v2.11, servicio Swarm dokploy-traefik |
| Configuración principal de Traefik | /etc/dokploy/traefik/traefik.yml |
| Directorio de configuración dinámica | /etc/dokploy/traefik/dynamic |
| Certificado HTTPS | Resolver letsencrypt, existente |
| Puntos de entrada de Traefik | web y websecure |

Acceso desde una máquina que ya tenga el alias y una identidad SSH administrativa autorizada:

~~~bash
ssh vps
cd /root/proyect/acropolis-channel
~~~

Sin alias:

~~~bash
ssh -p 22 root@72.61.37.46
cd /root/proyect/acropolis-channel
~~~

El alias pertenece al cliente SSH de la máquina; no se configura en GitHub Actions. Conocer la IP no proporciona acceso: otra IA debe utilizar un entorno con una identidad administrativa ya autorizada.

Huella ED25519 pública del servidor verificada al configurar:

~~~text
SHA256:gpDuoUgAnm80xRZoASKlnRc+uOh7BVy2IZuCRmDNs2k
~~~

Si cambia la huella, comprobar la nueva identidad por un acceso administrativo fiable. No desactivar la verificación de host para resolver un error.

## GitHub Actions y despliegue

Workflow: **.github/workflows/deploy.yml**, llamado **Deploy to VPS**. Se dispara por un push a main o mediante ejecución manual. Ambas opciones solo despliegan cuando la variable de repositorio DEPLOY_ENABLED tiene el valor literal true.

El job usa SSH nativo en ubuntu-latest. No hace checkout, instala dependencias ni construye imágenes en el runner. Los despliegues se serializan con el grupo acropolis-channel-production y cancel-in-progress: false; un despliegue activo no se interrumpe por un nuevo push. El límite actual del job es 30 minutos.

El script **scripts/deploy.sh** realiza exclusivamente:

~~~bash
#!/usr/bin/env bash
set -euo pipefail

cd /root/proyect/acropolis-channel
git pull --ff-only origin main
docker compose -p acropolis-channel up -d --build
~~~

El build necesario de los contenedores ocurre en el VPS y aprovecha la caché de Docker. Por decisión del propietario, no añadir pruebas, lint, validaciones previas, comprobaciones de salud ni un segundo build al workflow o al script.

El despliegue utiliza main tal como esté al ejecutar git pull, no fija el SHA del evento. Mantener la copia remota en main. El nombre de proyecto Compose acropolis-channel debe permanecer estable para no crear otro conjunto de contenedores, redes o volúmenes.

### Variables de repositorio

Se gestionan en GitHub → Settings → Secrets and variables → Actions → Variables.

| Variable | Valor inicial |
| --- | --- |
| VPS_HOST | 72.61.37.46 |
| VPS_PORT | 22 |
| VPS_USER | root |
| DEPLOY_ENABLED | false |

Estas variables son configuración no secreta. DEPLOY_ENABLED=false evita nuevas ejecuciones de despliegue; no detiene contenedores ya iniciados ni cancela un job activo.

### Secrets de repositorio

Se gestionan en GitHub → Settings → Secrets and variables → Actions → Secrets.

| Secret | Contenido |
| --- | --- |
| VPS_SSH_PRIVATE_KEY | Clave privada ED25519 exclusiva para este repositorio |
| VPS_SSH_KNOWN_HOSTS | Entrada SSH verificada del servidor 72.61.37.46 |

Los valores no están documentados ni versionados. La clave privada temporal se eliminó del VPS después de cargarla en GitHub. El workflow escribe ambos secrets en archivos temporales con permisos privados y los elimina al terminar.

La entrada pública de esta identidad en **/root/.ssh/authorized_keys** lleva el comentario **github-actions-acropolis-channel** y estas opciones:

~~~text
restrict,command="/bin/bash /root/proyect/acropolis-channel/scripts/deploy.sh"
~~~

La identidad solo puede ejecutar el comando de despliegue previsto, sin terminal ni reenvío de puertos. No es la identidad de acceso administrativo. El script y el código de main se ejecutan con permisos de root; limitar quién puede modificar esa rama y revisar cualquier cambio del proceso de despliegue.

Huella pública de la identidad de automatización inicial:

~~~text
SHA256:MNZm8EFNnu917miOmv4Y6MdGTId26in7sDgekKcUdds
~~~

La clave Actions → VPS no autentica al VPS ante GitHub. Actualmente el repositorio es público, por lo que el pull por HTTPS no necesita credenciales. Para publicar cambios desde el VPS se utiliza la sesión gh existente mediante un helper Git configurado solamente para este repositorio. La identidad de commits también es local al repositorio.

Si el repositorio pasa a privado, conservar o configurar por separado el acceso VPS → GitHub sin copiar tokens al README, al workflow ni al remoto de Git.

## Qué debe hacer la siguiente IA para publicar la aplicación

1. Conectarse por SSH, entrar en /root/proyect/acropolis-channel y revisar git status, la rama y la implementación actual. Preservar cambios locales antes de actualizar la copia.
2. Implementar la aplicación solicitada, su Dockerfile y un archivo **compose.yml** en la raíz. No hay stack ni puertos de aplicación elegidos en esta configuración inicial.
3. Definir en Compose los servicios, sus variables y sus volúmenes persistentes. Guardar valores privados solo en el VPS o en un gestor de secrets; .env y .env.* están ignorados por Git. Publicar únicamente .env.example sin credenciales reales.
4. Conectar los servicios públicos a la red externa dokploy-network con nombres o aliases únicos y estables, por ejemplo acropolis-channel-web. Los servicios internos que no deban publicarse pueden utilizar una red privada.
5. Solicitar o utilizar el dominio indicado por el propietario y configurar su DNS hacia el VPS.
6. Crear el routing propio de Traefik, desplegar una primera vez manualmente y comprobar contenedores, logs y URL real fuera del workflow.
7. Activar DEPLOY_ENABLED=true y ejecutar Deploy to VPS. Confirmar el resultado real del primer despliegue.

Declaración de la red existente en el futuro Compose:

~~~yaml
networks:
  dokploy-network:
    external: true
    name: dokploy-network
~~~

Asignarla explícitamente a los servicios que Traefik deba alcanzar. No crear otra red que sustituya a la existente.

### Relación real con Dokploy y Traefik

Este flujo replica el mecanismo operativo de Clarin: Docker Compose desde el VPS y publicación mediante el proxy de Dokploy. No crea automáticamente una aplicación administrada en el panel.

Traefik tiene providers.docker.swarmMode: true y un proveedor de archivos sobre /etc/dokploy/traefik/dynamic. La referencia viva **/etc/dokploy/traefik/dynamic/clarin.yml** apunta a contenedores por nombre. Para un Compose iniciado por SSH, no asumir que las labels de contenedores normales bastan para publicar la aplicación.

Crear un archivo propio:

~~~text
/etc/dokploy/traefik/dynamic/acropolis-channel.yml
~~~

Definir allí routers con el dominio real y un servicio con el nombre de red estable y el puerto interno correcto. Reutilizar web/websecure y el resolver letsencrypt según la configuración existente. No copiar rutas, credenciales ni configuración funcional de Clarin.

Antes de escribir ese archivo, comprobar si ya existe: el estado descrito aquí es el de la configuración inicial. No sobrescribir archivos de otros proyectos. Si se decide administrar el proyecto desde el panel o usar una API/webhook de Dokploy, actualizar el workflow y evitar que ambos mecanismos desplieguen a la vez.

### Activación y ejecución

Con la aplicación y su publicación preparadas, usar la interfaz de GitHub o una sesión gh autenticada:

~~~bash
gh variable set DEPLOY_ENABLED --body true --repo ricardoalejandro/acropolis-channel
gh workflow run deploy.yml --ref main --repo ricardoalejandro/acropolis-channel
~~~

Para consultar la variable, compatible con el gh instalado:

~~~bash
gh api repos/ricardoalejandro/acropolis-channel/actions/variables/DEPLOY_ENABLED --jq .value
~~~

Desactivar siguientes despliegues:

~~~bash
gh variable set DEPLOY_ENABLED --body false --repo ricardoalejandro/acropolis-channel
~~~

## Cuándo modificar Actions a medida que crezca el proyecto

| Cambio | Lugar habitual |
| --- | --- |
| Nuevas funcionalidades, librerías o código | Aplicación y Dockerfile |
| Nuevos servicios, puertos, redes, volúmenes o variables | compose.yml y configuración privada del VPS |
| Cambios de dominio o routing | Archivo propio de Traefik |
| Comandos adicionales necesarios para desplegar | scripts/deploy.sh |
| Otro VPS, usuario o puerto; rotación de claves | Variables/secrets y authorized_keys |
| Otra rama, varios entornos, imágenes en un registry o despliegue mediante Dokploy API | .github/workflows/deploy.yml y documentación |
| Una construcción que exceda el límite actual | timeout-minutes del workflow, después de revisar el proceso |

El crecimiento normal de la aplicación no requiere cambiar Actions. Mantener el workflow como transporte SSH y el script como operación remota mientras este mecanismo siga siendo suficiente.

Si cambia la ruta del proyecto o del script, actualizar también el comando forzado de la identidad SSH; modificar solo el workflow no basta. Si cambia el VPS, volver a preparar su carpeta, acceso, Docker, secrets y huella de host verificada.

Para rotar la clave dedicada, generar otra ED25519 fuera de Git, sustituir exclusivamente su entrada identificada en authorized_keys, actualizar VPS_SSH_PRIVATE_KEY, comprobar autenticación y eliminar el archivo privado temporal. Preservar las demás identidades administrativas.

## Operación y resolución de problemas

Despliegue manual, solo cuando ya exista Compose:

~~~bash
cd /root/proyect/acropolis-channel
/bin/bash scripts/deploy.sh
~~~

Comprobaciones manuales posteriores, fuera del script:

~~~bash
cd /root/proyect/acropolis-channel
docker compose -p acropolis-channel ps
docker compose -p acropolis-channel logs --tail=100
gh run list --workflow deploy.yml --limit 5 --repo ricardoalejandro/acropolis-channel
~~~

- **Job omitido:** comprobar DEPLOY_ENABLED; false es intencional mientras no existe la aplicación.
- **Permission denied (publickey):** revisar el secret y la entrada dedicada de authorized_keys mediante acceso administrativo.
- **Host key verification failed:** verificar la identidad real del servidor antes de actualizar VPS_SSH_KNOWN_HOSTS.
- **git pull falla:** revisar cambios locales o historia divergente y preservarlos antes de resolver. No introducir git reset --hard en el despliegue.
- **No configuration file provided:** todavía falta compose.yml o la ruta de ejecución es incorrecta.
- **Contenedores iniciados, web inaccesible:** revisar DNS, red externa, alias, puerto interno, configuración dinámica de Traefik y logs.

## Reglas para futuras intervenciones

- Operar desde /root/proyect/acropolis-channel y comprobar el estado real antes de editar.
- No instalar ni descargar herramientas cuando las existentes basten.
- No imprimir ni versionar claves, tokens, archivos .env o datos privados.
- Preservar trabajo local, configuraciones ajenas, datos y volúmenes.
- No añadir docker compose down -v, purgas ni eliminaciones de datos al despliegue.
- No activar despliegues mientras no exista una aplicación y una configuración Compose utilizable.
- Mantener el workflow y el script exclusivamente para despliegue, según la decisión del propietario.
- Actualizar este README cuando cambien el dominio, servicios, infraestructura, acceso o proceso.
- Distinguir configuración preparada, ejecución de despliegue y salud real: informar solo lo comprobado.

## Referencias

- [GitHub: concurrencia de workflows](https://docs.github.com/en/actions/concepts/workflows-and-actions/concurrency)
- [GitHub: secretos de Actions](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets)
- [Dokploy: dominios y redes de Compose](https://docs.dokploy.com/docs/core/docker-compose/domains)
