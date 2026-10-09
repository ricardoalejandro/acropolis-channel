# Desarrollo de Acrópolis Channel en WSL

El checkout de desarrollo está en `/home/rrojacam/projects/acropolis-channel`, Ubuntu-24.04. Codex se utiliza desde Windows y ejecuta las herramientas Linux mediante `wsl.exe`. El repositorio es `https://github.com/ricardoalejandro/acropolis-channel`, rama `main`.

Producción y su operación permanecen en `/root/proyect/acropolis-channel` del VPS. Las reglas de producto y calidad siguen en [AGENTS.md](../AGENTS.md), [calidad](quality.md) y las skills versionadas. La carpeta `C:\proyectos\003.ACROPOLIS-CHANNEL` conserva artefactos y la entrada administrativa histórica.

## Acceso desde Windows

La carpeta abierta en Codex corresponde a:

```text
\\wsl$\Ubuntu-24.04\home\rrojacam\projects\acropolis-channel
```

Ejecutar Git, Bash, Python y Docker dentro de Ubuntu; abrir la carpeta UNC no convierte la consola de Windows en una consola Linux. Por ejemplo, desde PowerShell:

```powershell
wsl -d Ubuntu-24.04 --cd /home/rrojacam/projects/acropolis-channel --exec /bin/bash -lc 'git status --short --branch'
wsl -d Ubuntu-24.04 --cd /home/rrojacam/projects/acropolis-channel --exec /bin/bash -lc 'docker info && docker compose version'
wsl -d Ubuntu-24.04 --cd /home/rrojacam/projects/acropolis-channel --exec /bin/bash -lc 'bash scripts/verify.sh --preflight'
```

Git de Windows confía únicamente en esta ruta UNC mediante una entrada `safe.directory` específica. El repositorio usa `core.autocrlf=false` para conservar finales de línea Linux y `core.filemode=false` para evitar diferencias ficticias de permisos al leerlo desde Windows. Los permisos existentes y los bits ejecutables del índice se conservan. Al añadir un script ejecutable nuevo, declarar ese permiso con `git update-index --chmod=+x RUTA` y comprobar el archivo en Linux. Ejecutar las operaciones Git de trabajo dentro de WSL. Si Git Windows sobre UNC devuelve errores como `Function not implemented` al consultar `.git/sequencer/todo`, comprobar el estado con Git Linux antes de interpretar una operación pendiente; no borrar metadatos para corregir una diferencia de lectura. Cuando haga falta la autenticación GitHub ya existente de Windows para fetch/push, usarla sin copiar credenciales a WSL ni al repositorio.

Si el entorno aislado de Windows devuelve `Wsl/E_ACCESSDENIED`, solicitar la ejecución autorizada fuera de ese aislamiento para la operación concreta. Esto es distinto de instalar o iniciar sesión en Codex dentro de Linux. No copiar la sesión de Codex, credenciales SSH/Git ni autenticaciones del VPS a WSL.

## Herramientas y datos locales

Ubuntu dispone de Git, Bash y Python3. Docker Engine, Buildx y Compose se instalan desde el [repositorio oficial de Docker para Ubuntu](https://docs.docker.com/engine/install/ubuntu/). El daemon local utiliza el [modo rootless](https://docs.docker.com/engine/security/rootless/) del usuario `rrojacam`, contexto `rootless`; el servicio se administra con `systemctl --user`.

```bash
docker context show
systemctl --user status docker --no-pager
docker info
docker compose version
# Si el servicio del usuario está detenido:
systemctl --user start docker
```

No hace falta añadir el usuario al grupo Docker ni darle sudo sin contraseña. .NET 10 SDK y Node 22 se usan en las imágenes Docker del repositorio; PostgreSQL 18 tiene una base propia para el runtime local y bases independientes para QA. No usar el npm de Windows que pueda aparecer por herencia del PATH de WSL.

El traslado trae sólo el repositorio Git. `.env`, `.local`, datos de cuentas/contenidos, volúmenes, backups, certificados PFX y keyring productivos permanecen en el VPS. Cada entorno guarda sus propios artefactos privados en `.local/`, fuera de Git. El punto de continuación local es `.local/continuation-current.md`; no crear punteros locales que hagan pasar un manifiesto del VPS por un despliegue WSL.

## Servicios persistentes en localhost

El propietario solicita poder pedir cambios y revisarlos con servicios locales levantados en este checkout. `compose.local.yml` utiliza exclusivamente el proyecto `acropolis-channel-local`, redes propias y estos puertos fijos, publicados sólo en `127.0.0.1`:

| Servicio | Dirección |
| --- | --- |
| Aplicación y API con HTTPS | `https://localhost:17443` |
| Redirección a la aplicación HTTPS | `http://localhost:17480` |
| Buzón Mailpit de desarrollo | `http://localhost:17425` |

PostgreSQL (`acropolis_local`) y SMTP TLS/autenticado (`mailpit:465`) sólo existen en la red privada; no publican puertos del host. El correo local se captura en Mailpit y no se entrega por el SMTP productivo. Los recorridos de registro y confirmación se realizan con la interfaz y sus correos normales, sin confirmaciones automáticas ni semillas QA. Seguimiento de consumo y avisos de suscripción permanecen deshabilitados por defecto.

Desde `pwsh` (PowerShell 7 disponible en este entorno), en la raíz del checkout; el wrapper usa sus API .NET de certificados y no debe ejecutarse con Windows PowerShell 5.1:

```powershell
./scripts/local-runtime.ps1 up
# Reutilizar imágenes sólo cuando la fuente de aplicación no cambió:
./scripts/local-runtime.ps1 up -NoBuild
./scripts/local-runtime.ps1 status
# Instalar/verificar el certificado ya generado, sin reiniciar servicios:
./scripts/local-runtime.ps1 trust
./scripts/local-runtime.ps1 check
./scripts/local-runtime.ps1 logs
./scripts/local-runtime.ps1 stop
```

El wrapper usa WSL para el runtime y comprueba también los listeners de Windows antes de ocupar los puertos fijos. Cada `up` detiene únicamente este proyecto local antes de comprobarlos y conserva sus volúmenes; también recupera un arranque anterior incompleto. El comando `trust` es exclusivo del wrapper Windows; Python no modifica almacenes de certificados Windows. Dentro de WSL:

```bash
python3 scripts/local-runtime.py up
python3 scripts/local-runtime.py check
# Reutilizar las imágenes locales existentes:
python3 scripts/local-runtime.py up --no-build
```

`up` prepara las imágenes locales usando Docker, PKI, base y migraciones del runner, y levanta la aplicación con configuración ASP.NET `Production` pero variables exclusivamente locales. `stop` detiene los servicios conservando sus datos. `status`, `check` y `logs` permiten inspeccionar disponibilidad y diagnóstico sin mostrar las variables privadas. Estos helpers no cargan `.env` de producción ni utilizan `compose.yml`.

Las credenciales `LOCAL_*` se generan y conservan en `.local/runtime/.env`, con permisos privados y fuera de Git. Los volúmenes `acropolis_channel_local_database`, `acropolis_channel_local_keyring` y `acropolis_channel_local_pki` mantienen datos, claves y certificados entre reinicios. Las redes locales, también etiquetadas `acropolis.environment=local`, son `acropolis_channel_local_private` y `acropolis_channel_local_publication`; no se conectan a Traefik ni a SMTP productivo. No borrar o regenerar el protector/keyring de una base persistente como forma rutinaria de resolver un fallo.

El certificado web es una hoja autofirmada `CA:FALSE`, limitada a localhost y direcciones de loopback. Se exporta únicamente su parte pública a `.local/runtime/export/localhost.crt`. El wrapper Windows valida su identidad y gestiona la confianza de ese certificado exacto en el almacén `Cert:\CurrentUser\Root`. La primera instalación puede mostrar una confirmación de Windows: aceptar únicamente `Acropolis Localhost Development`. `trust` instala o verifica el certificado ya generado sin detener ni recrear los servicios; `up` reutiliza ese mismo procedimiento. El importador tiene una espera máxima de 45 segundos y el recibo privado sólo se guarda después de comprobar la huella exacta en el almacén. Si la confirmación no se completa, seguir el comando `Import-Certificate` que muestra el error desde una ventana interactiva de PowerShell y volver a ejecutar `check`. La CA SMTP separada y sus claves permanecen dentro del volumen privado de PKI y no se importan en Windows. No usar excepciones TLS del navegador ni trasladar certificados productivos para abrir localhost.

Un error `NET::ERR_CERT_AUTHORITY_INVALID` en localhost requiere revisar la confianza antes de modificar o regenerar certificados: ejecutar `trust` y luego el `check` Windows. El `check` Python valida HTTPS con la hoja local explícita y no comprueba `CurrentUser/Root`. Un recibo `windows-trust.json` registra una observación, no garantiza que la hoja siga instalada. No repetir importadores bloqueados sin comprobar su proceso; el helper acotado termina su propio hijo y explica cómo completar la confirmación interactiva.

`check` indica explícitamente si Windows todavía no confía en el certificado local y verifica HTTPS con la validación TLS normal. Tras instalarlo, volver a cargar la pestaña de `https://localhost:17443`; la confianza debe estar comprobada antes de considerar el sitio local accesible sin advertencias.

`check` comprueba el runtime local y sus recorridos técnicos reales; pasar esa comprobación no constituye un gate completo ni un certificado elegible para publicación. La disponibilidad actual y cualquier revisión visual deben registrarse como resultados observados, separados de la preparación de estos helpers.

## Validación y próximas sesiones

1. Leer AGENTS y `.local/continuation-current.md`, revisar `git status` y cargar la skill correspondiente.
2. Sincronizar con `git fetch origin` y fast-forward cuando el estado lo permita. Conservar los cambios existentes y evitar reset, clean o push forzado.
3. Consultar `status` y `check` del runtime existente; desde Windows usar también el `check` del wrapper para comprobar confianza TLS, redirección y buzón. Si está saludable, reutilizarlo. Si hay que iniciarlo o incorporar cambios de aplicación, ejecutar `up`; no reutilizar imágenes anteriores a esos cambios mediante `-NoBuild`/`--no-build`. Resolver una confianza TLS pendiente con `trust` sin reiniciar servicios. Desarrollar/probar según el riesgo. El preflight verifica sintaxis, pruebas administrativas, Compose, formato, tipos, lint y auditoría frontend con Node en Docker; ese procedimiento no levanta aplicación, PostgreSQL ni SMTP y su informe siempre es inelegible para despliegue.
4. Leer el `report.json` nuevo y comprobar `status`, `scope`, `deployment_eligible` y `cleanup_complete`. Los informes están en `.local/qa/<sha>/<run>/`.

La fuente de aplicación y el Dockerfile son comunes a local y producción; las recetas separadas aplican las variables de cada entorno. Versionar y sincronizar los cambios necesarios de aplicación, receta e instrucciones mediante GitHub, con su validación correspondiente. Guardar en la continuidad privada los SHA, huellas instaladas, resultados y procesos observados; no convertir el estado de una sesión en una regla permanente.

El runtime persistente permite comprobar cambios locales con aplicación, PostgreSQL y buzón propios. La receta completa de QA con navegador, carga y restauración conserva el procedimiento documentado del VPS; no atribuirle una ejecución local por haber pasado `check` o preflight. La compatibilidad completa del gate con rootless requiere su propia validación cuando el trabajo lo necesite.

`compose.local.yml` es la receta de desarrollo persistente y se opera mediante el helper local. `compose.yml` es la configuración productiva y requiere la red externa `dokploy-network`, secretos y proxy del VPS; no ejecutarla en WSL como receta de desarrollo. `compose.qa.yml` es utilizado por los scripts de QA con variables y datos sintéticos propios; no sustituirlas por las productivas ni usar el runtime persistente como base de QA.

## Publicar cuando se solicite

GitHub transporta el código entre WSL y VPS. El despliegue se prepara y ejecuta en el VPS mediante SSH estricto desde Windows:

```powershell
ssh -o BatchMode=yes -o StrictHostKeyChecking=yes vps
```

Cuando el propietario solicite subir cambios a producción, seguir [acropolis-vps-deploy](../.agents/skills/acropolis-vps-deploy/SKILL.md): consultar continuidad/manifiesto reales del VPS, sincronizar mediante Git sin descartar cambios, validar allí el candidato definitivo según su riesgo y publicar las imágenes certificadas sin reconstrucción. Usar las variables productivas que permanecen en el servidor; no copiar las variables, PKI, base o certificado del runtime local. El certificado acotado de frontend depende además de la versión publicada y de la evidencia privada del VPS.

Levantar o actualizar localhost y editar estas instrucciones no publica cambios ni autoriza una nueva entrega productiva. La entrega del editor de 8 de octubre de 2026 ya concluyó; verificar el estado actual antes de interpretar autorizaciones históricas.
