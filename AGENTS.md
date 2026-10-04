# Guía de trabajo para Acrópolis Channel

El propietario define el objetivo; sus indicaciones actuales prevalecen sobre decisiones archivadas. Este AGENTS fija el marco general y las skills detallan procedimientos dentro de él. La guía local de Windows sólo permite llegar a estas instrucciones, no constituye otro nivel de mando.

## Inicio de cada sesión

El código canónico está exclusivamente en `/root/proyect/acropolis-channel`, repositorio `https://github.com/ricardoalejandro/acropolis-channel`, rama `main`. Desde Windows conectar con `ssh -o BatchMode=yes -o StrictHostKeyChecking=yes vps`; el alias está configurado en esa máquina. Si la sesión ya corre en el VPS, trabajar directamente en esta carpeta. `C:\proyectos\003.ACROPOLIS-CHANNEL` sólo contiene la guía de acceso, skills de entrada y artefactos de revisión.

Leer este archivo, `.local/continuation-current.md` y `.local/vps-deployment.md` para retomar. Consultar `.local/last-active-deployment` y el manifiesto al que apunta para conocer el runtime real; `.local/last-deployment` registra la última publicación verificada. El HEAD de Git puede incluir documentación posterior al SHA desplegado. No confundir un commit publicado, QA aprobada y una versión en servicio.

Las skills canónicas están versionadas en `.agents/skills/acropolis-quality/SKILL.md` y `.agents/skills/acropolis-vps-deploy/SKILL.md`. Las entradas locales de Windows las cargan por SSH; actualizar aquí las reglas completas para evitar copias divergentes. Las rutas `docs/`, `scripts/` y `.local/` mencionadas por las skills corresponden a este checkout del VPS.

## Trabajo y arquitectura

- Revisar estado Git, rama y remoto; fetch y `git pull --ff-only origin main` cuando sea compatible. Conservar cambios, archivos no versionados y commits existentes; analizar divergencias sin reset hard, clean ni push forzado.
- Monolito modular ASP.NET Core / .NET10 LTS, React19 con TypeScript/Vite y PostgreSQL18. SDK .NET10 y Node22 sólo en Docker; conservar Node20 compartido del host. Leer `docs/architecture.md` y los documentos del módulo antes de cambiar contratos.
- Módulos actuales: Platform, Identity y Catalog. Los módulos son dueños de sus schemas/historias; Application no depende de infraestructura ni se leen tablas internas de otro módulo. Migraciones sólo mediante runner y bloqueo exclusivo, nunca desde cada instancia web.
- Aplicar la skill acropolis-quality al programar o validar. Cada proceso requiere pruebas útiles de reglas, aplicación, PostgreSQL real, HTTP, interfaz, autorización y fallos; incluir concurrencia/idempotencia cuando corresponda. No añadir módulos vacíos o pruebas ficticias.
- Preservar las reglas Graphify existentes; `.graphify/` es generado y permanece fuera de Git/Docker. El código anterior en Node fue sustituido; no restaurar ese servidor ni GitHub Actions/credenciales dedicadas. Conservar el historial.

## Producto, identidad y diseño

- La versión publicada ya incorpora diseño adaptable, cuentas/MFA y catálogo editorial persistente. El estado de cuentas y contenidos debe consultarse; no tratar datos vacíos como un fallo ni poblar producción con semillas QA.
- El propietario retomó SMTP el 4 de octubre de 2026 y autorizó correo propio naperu.cloud en un servicio independiente del VPS. Leer `docs/mail-operations.md`. Mantener `IDENTITY_EMAIL_ENABLED=false` hasta verificar DNS/PTR/TLS y entrega controlada; las credenciales se generan y almacenan sólo en el VPS, sin pedirlas por chat. Registro, reenvío y solicitud de recuperación quedan deshabilitados; cuentas confirmadas existentes pueden entrar. No crear confirmaciones o accesos por atajo.
- Protector PFX privado, key ring persistente, HTTPS y proxies exactos siguen siendo obligatorios sin correo. MFA TOTP es obligatorio para `Users.Manage` y `Content.Manage`; niveles institucionales, permisos y suscripciones son independientes. Primer administrador sólo para una cuenta exacta confirmada y designada.
- Catálogo: Lecturas, Documentales, Videos, Podcast, Charlas online y Cursos. Sólo metadatos y sinopsis públicas; borradores/archivados no se exponen y el material restringido no se guarda en sinopsis. Publicación con concurrencia, slug estable y auditoría atómica.
- Diseño sobrio, elegante y contemporáneo para jóvenes, acorde con Nueva Acrópolis. Conservar la estructura del sitio oficial acropolischannel.pe; BBVA Aprendemos Juntos y DocPlus son inspiración visual. Leer `docs/design.md`, preservar la dirección aprobada y revisar móvil, teclado, contraste, cargas y errores reales.
- Reproducción/acceso AWS, membresías, pagos/facturación, migración de datos siguen pendientes de contratos e integraciones. SMTP propio está autorizado y su activación depende de las guardas de correo. No inventar precios, reglas de acceso o proveedores. `dist-preview` y sus demostraciones permanecen separados del build productivo; no crear recursos AWS/DNS sin solicitud.

## Validación y publicación

- Para cambios de código, scripts, migraciones o configuración de ejecución/construcción: commit completo y `bash scripts/verify.sh` con checkout limpio antes de publicar. `--working-tree` sirve para iterar, pero es ineligible para despliegue.
- QA usa recursos `acropolis_test_*`, credenciales/datos sintéticos, PostgreSQL real y CA/SMTP propios; nunca cargar `.env` productivo, montar datos de producción o el socket Docker. En este VPS compartido ejecutar una sola suite/carga pesada a la vez.
- Informes privados en `.local/qa/<sha>/<run>/report.json`; SHA e IDs exactos de web/migraciones deben coincidir, con passed y deployment_eligible=true. No rebajar umbrales ni ignorar fallos. Las cargas con 100.000 cuentas/10.000 fichas y 50 sesiones/lectores no acreditan 1.000 concurrentes ni streaming.
- Cambios exclusivamente de instrucciones/documentación se validan por contenido, rutas y formato de skills; no reconstruyen, redepliegan ni requieren repetir cargas ya aprobadas. Un commit sólo documental no hereda certificación de imágenes para su nuevo SHA. Un futuro despliegue sigue exigiendo QA del SHA que deploy.sh acepta.
- Para preparar, revisar o ejecutar un despliegue aplicar acropolis-vps-deploy. Desplegar sólo si solicitado; una autorización vigente basta. No inferir despliegue por actualizar código, instrucciones o QA.
- Dominio `https://acropolischannel.naperu.cloud`, Compose `acropolis-channel`, web8080, alias `acropolis-channel-web`, red externa `dokploy-network`. DB privada sin puerto del host y roles separados. `scripts/deploy.sh --expected-sha SHA` utiliza las imágenes aprobadas sin reconstruir.
- Routing exclusivamente en `/etc/dokploy/traefik/dynamic/acropolis-channel.yml`; conservar configuración global/ACME/otros routers. Verificar DNS, TLS normal, redirect, interfaz/API. Si una política bloquea el navegador, respetarla y declarar la revisión visual pendiente; no sustituirla con una automatización que eluda el bloqueo.

## Infraestructura y continuidad

Guardar las decisiones duraderas del propietario en este AGENTS o en la documentación del módulo correspondiente del VPS; el avance y pendientes en `.local/continuation-current.md`. La carpeta local conserva sólo acceso y referencia al punto vigente. Registrar decisiones útiles para retomar, no una copia indiscriminada de mensajes ni secretos.

- `.env`, `.local/`, claves, dumps y reportes son privados y no entran en Git/imágenes. Inspeccionar sólo datos necesarios sin mostrar contraseñas, semillas/QR/códigos MFA, enlaces de tokens ni configuración completa sensible.
- Preservar SSH administrativo, autenticación GitHub, otros proyectos, datos y volúmenes. No purgar Docker ni down-v producción. Respaldar DB, key ring, PFX y configuración conjuntamente; conservar recuperación de imagen/Compose/env/routing compatibles. No bajar migraciones automáticamente.
- Restaurar una base antigua exige web detenido, invalidación y revalidación explícita conforme a `docs/identity-operations.md` y `docs/mfa-operations.md`; no reactivar sesiones/credenciales/MFA del backup ni usar bootstrap como recuperación.
- Al cerrar o pausar, registrar commit/Git, runtime real, informes, trabajo pendiente y procesos propios en `.local/continuation-current.md`; actualizar la guía local si corresponde. Consultar procesos/manifiestos antes de repetir una operación interrumpida por SSH. Una QA cortada no certifica despliegue. Mantener el detalle administrativo en `.local/vps-deployment.md`, sin duplicarlo en documentación pública.
