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

Los servidores de prueba unitarios y el runner usan Testing. El candidato HTTP usa Production para ejercitar la configuración desplegable, con conexión QA explícita y `AllowedHosts` limitado a los nombres internos utilizados por las pruebas. Esto no lo conecta a producción.

Los servicios limitan CPU, memoria y procesos; las construcciones .NET usan un único procesador/MSBuild sin paralelismo. Playwright tiene memoria compartida propia, no IPC del host. La limpieza, incluso después de un fallo, selecciona únicamente los nombres aleatorios creados por esa ejecución y retira sus contenedores, redes y volúmenes de pruebas. No purga Docker ni elimina datos de otros proyectos. Se conservan las imágenes candidatas y los informes para revisión y reutilización.

## Comprobaciones obligatorias

| Área | Comprobación |
| --- | --- |
| Scripts | Sintaxis Bash de scripts e inicialización PostgreSQL; sintaxis de helpers Python administrativos |
| Orquestación del despliegue | Tests Python con mocks y directorios temporales para SHA, estado Git, artefacto, DNS bloqueado y recuperación; no contactan Docker, Git, DNS ni producción |
| Imagen de ejecución | Usuario no root; sin fuente, `.env`, `.local`, metadata Git, node_modules, Node, Git ni SDK .NET; comprobación sin red y en modo read-only |
| Backend | Restore NuGet `--locked-mode`, build Release, formato y auditoría de dependencias transitivas; vulnerabilidades High/Critical bloquean |
| Unidad backend | Casos reales de saludo y decisión de readiness, con cobertura de líneas y ramas de `Acropolis.Platform.Application` de al menos 80% |
| HTTP y arquitectura | Tests del contrato HTTP, fallos sanitizados y dependencias permitidas |
| Integración | PostgreSQL real, configuración Testing obligatoria, base `acropolis_test_*` y credencial QA propia; sin fallback ni tests omitidos por falta de base; cobertura de líneas y ramas de Infrastructure de al menos 80%, excluyendo migraciones generadas y snapshot |
| Frontend | `npm ci`, typecheck, lint, formato, tests, cobertura mínima 80% en líneas, ramas, funciones y statements; build y auditoría npm High/Critical |
| Migraciones | Antes de migrar readiness 503 y liveness 200; primera migración desde base vacía; segunda ejecución sin cambios de schema o historial |
| Navegador | Playwright desktop/móvil contra frontend y API del candidato real; resultados y reporte conservados |
| Resiliencia | Al detener PostgreSQL readiness 503 y liveness 200; al recuperarlo readiness 200; reinicio de app y base con schema e historial persistentes |
| Recuperación | `pg_dump` custom, restauración en otro proyecto QA, reaplicación idempotente y comparación de schema e historial con el origen; intento de restaurar producción rechazado antes de contactar Docker |
| Carga | k6: cinco iteraciones breves de calentamiento antes de la medición; 20 VU durante 60 segundos, pausa de 1 segundo por iteración, p95 HTTP menor de 500 ms, tasa de errores HTTP 0 y todos los checks correctos |

La cobertura de Application mide decisiones de la capa sin infraestructura. Las métricas de líneas y ramas deben existir y ser válidas; un informe sin ramas reales se declara no aplicable para esa métrica. Los tests de PostgreSQL y del runner ejercitan SQL y permisos con una base real; no se contabilizan como unidad ni se reemplazan por mocks para aumentar el porcentaje.

La carga utiliza el saludo y readiness reales. El umbral describe este escenario inicial y no es una promesa de capacidad para futuros módulos. El resumen contiene mediciones observadas. El runner utiliza la [imagen oficial de k6](https://grafana.com/docs/k6/latest/set-up/install-k6/) con una versión fijada en Compose.

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
  --input /root/proyect/acropolis-channel/.local/backups/ARCHIVO.dump
```

Sustituir `RUN` por el identificador real en minúsculas y `ARCHIVO` por un backup existente. El destino requiere los roles de inicialización QA. La restauración usa `acropolis_migrator` como propietario, sin trasladar propietarios o grants ajenos; ejecutar después el runner de migraciones de ese mismo ámbito QA para verificar y restablecer los permisos propios del historial. El script no permite una restauración sobre el proyecto de producción.
