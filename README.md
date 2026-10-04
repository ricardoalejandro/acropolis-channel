# Acrópolis Channel

Base técnica del monolito modular: ASP.NET Core / .NET 10 LTS, React 19 con TypeScript y Vite, PostgreSQL 18. La aplicación y la base de datos se ejecutan en el VPS; la distribución multimedia futura estará en AWS.

## Trabajo y alcance inicial

El checkout canónico está en `/root/proyect/acropolis-channel`; rama `main`, remoto HTTPS `https://github.com/ricardoalejandro/acropolis-channel`. La carpeta Windows contiene instrucciones, nunca otra copia del código. Conectar con el alias existente:

```powershell
ssh -o BatchMode=yes -o StrictHostKeyChecking=yes vps
```

La entrega inicial muestra «Hola mundo» obtenido de una API real. Identidad, contenidos, suscripciones, pagos, facturación e integraciones son módulos posteriores; no hay registro, cobros ni migración de WordPress implementados.

- Backend y migraciones: `src/`.
- Interfaz, componentes y navegador: `frontend/`.
- Pruebas backend: `tests/backend/`; carga: `tests/load/`.
- Límites de módulos: [arquitectura](docs/architecture.md).
- Pruebas y criterios de calidad: [QA](docs/quality.md).

## Contratos HTTP

| Ruta | Resultado |
| --- | --- |
| `/` | Interfaz React responsive con carga, saludo, error y reintento |
| `/api/v1/greeting` | `200 {"message":"Hola mundo"}` |
| `/health` | Liveness `200 {"status":"ok"}`; independiente de PostgreSQL |
| `/health/ready` | `200 {"status":"ok"}` cuando PostgreSQL y migraciones están listos; `503` en caso contrario |

Las API desconocidas devuelven 404; el fallback de SPA no las oculta. Los errores no exponen excepciones ni credenciales. OpenAPI se limita a desarrollo. React y API comparten origen: las solicitudes usan rutas relativas.

## Calidad desde el VPS

No hay GitHub Actions. Todo se valida con contenedores, sin instalar SDK .NET ni cambiar Node.js del host. El frontend se construye con Node.js 22 en Docker; el servidor de producción es ASP.NET Core en el puerto interno 8080.

Durante desarrollo puede ejecutarse `bash scripts/verify.sh --working-tree`. Su informe no autoriza desplegar una copia sin commit. Para una entrega:

```bash
cd /root/proyect/acropolis-channel
git status --short --branch
git fetch origin
# Sincronizar por fast-forward antes de editar, conservando trabajo previo.
# Crear el commit local candidato cuando el cambio esté completo.
bash scripts/verify.sh
# Publicar main únicamente si todas las comprobaciones pasan.
git push origin main
bash scripts/deploy.sh --expected-sha "$(git rev-parse HEAD)"
```

QA utiliza PostgreSQL real y datos sintéticos en proyecto, red y volúmenes propios; no carga `.env` de producción ni monta sus volúmenes o el socket Docker. Los informes privados quedan en `.local/qa/<sha>/<run>/`. Cada proceso nuevo requiere pruebas de reglas, casos de uso, persistencia, acceso, fallos y recorrido funcional; no agregar pruebas vacías.

## Configuración y publicación

Crear `.env` exclusivamente en el VPS, siguiendo `.env.example`, con contraseñas distintas y permisos 600. Nunca publicar `.env`, `.local/`, dumps, informes con datos privados ni secretos. `PUBLIC_VPS_IPV4` se configura privadamente para comprobar DNS.

Compose conserva proyecto `acropolis-channel`, servicio `web`, alias `acropolis-channel-web` y red externa `dokploy-network`. PostgreSQL está en una red privada sin puerto publicado. La aplicación usa un rol sin DDL; las migraciones usan otro rol limitado a esta base. Las migraciones se ejecutan por separado, con exclusión mutua.

El script de despliegue requiere checkout limpio en main, SHA esperado, coincidencia con origin y un informe QA aprobado que corresponda a los identificadores exactos de ambas imágenes. No reconstruye después de QA. Guarda una recuperación privada, respalda la base, ejecuta migraciones, arranca la aplicación y verifica readiness.

La URL objetivo es **https://acropolischannel.naperu.cloud**. La publicación usa exclusivamente `/etc/dokploy/traefik/dynamic/acropolis-channel.yml`, a partir del asset propio `infra/traefik/acropolis-channel.yml`. No modificar Traefik global, otros routers ni DNS como parte del despliegue. DNS incorrecto pospone una nueva publicación ACME; el resultado interno se informa por separado.

La publicación requiere certificado válido, redirección HTTP, interfaz y contratos públicos correctos. No aceptar TLS con `-k`. Una construcción exitosa no equivale a un despliegue verificado.

## Recuperación y datos

Los manifiestos y snapshots están en `.local/deployments/`; `.local/last-deployment` indica la última publicación completada. Preservar imágenes anteriores y su routing compatible. El rollback no ejecuta migraciones descendentes ni elimina datos. Si una versión anterior no admite el esquema actual, detener y aplicar el procedimiento documentado de recuperación.

Para una copia manual:

```bash
bash scripts/backup-db.sh --project acropolis-channel --database acropolis --output /root/proyect/acropolis-channel/.local/backups/acropolis.dump
```

La restauración automatizada está restringida a proyectos y bases de pruebas `acropolis_test_*`. Verificar primero allí una recuperación; no restaurar producción ni borrar volúmenes como operación rutinaria.

Los 100.000 usuarios registrados y aproximadamente 1.000 conectados son un objetivo del producto. La carga limitada de esta base es una referencia técnica, no una acreditación de capacidad para los procesos futuros.
