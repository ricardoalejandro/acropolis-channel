---
name: acropolis-vps-deploy
description: "Preparar, desplegar o diagnosticar Acropolis Channel directamente en su VPS con imágenes certificadas por QA, Docker Compose y Traefik."
---

# Despliegue de Acropolis Channel

## Inicio y autorización

Trabajar en `/root/proyect/acropolis-channel`, main de `https://github.com/ricardoalejandro/acropolis-channel`. Desde Windows conectar con `ssh -o BatchMode=yes -o StrictHostKeyChecking=yes vps`; en el VPS operar directamente. Leer `AGENTS.md`, `.local/continuation-current.md` y `.local/vps-deployment.md`. Las rutas siguientes son del checkout canónico.

Desplegar cuando la solicitud lo requiera; una autorización vigente basta. Código, QA, push o actualización de instrucciones no autorizan por sí solos despliegue. No crear/modificar DNS, recursos AWS u otros proyectos sin solicitud. Conservar ausencia de Actions y runtimes compartidos.

Consultar `.local/last-active-deployment` y su manifiesto para el runtime real; `.local/last-deployment` para publicación verificada. HEAD puede ser posterior por documentación: no asumir que Git y la imagen pública son el mismo SHA ni repetir una operación sin revisar proceso/log/manifest.

## Preparación y certificación

- Revisar Git limpio/rama/origin y sincronizar ff-only cuando sea compatible, conservando cambios y commits. Nunca reset hard, clean o push forzado.
- Aplicar [acropolis-quality](../acropolis-quality/SKILL.md) cuando el candidato no tenga certificación vigente. No repetir el gate de una imagen inmutable ya certificada salvo cambios o preocupaciones nuevas.
- `deploy.sh` exige HEAD=origin/main=SHA esperado y reporte privado `.local/qa/<sha>/<run>/report.json` passed/eligible con IDs exactos de web y migraciones. Un commit documental posterior no hereda certificación para su nuevo SHA. Publicar el candidato aprobado antes de ejecutar deploy.
- Mantener Compose `acropolis-channel`, web8080/alias `acropolis-channel-web`, red `dokploy-network`, PostgreSQL 18 privado y roles separados. .NET 10/Node 22 sólo Docker; conservar Node 20 host. Migraciones sólo runner con bloqueo.
- Antes de modificar `.env` conservar una copia privada; registrar imagen/configuración activa y recursos ajenos necesarios para comprobar preservación. Inspeccionar valores sensibles sólo dentro del proceso, con salida de presencia/validez y sin mostrar secretos.

## Identidad e integración SMTP propia

Leer `docs/identity-operations.md`. SMTP propio está autorizado: leer `docs/smtp-integration.md`. El servidor/webmail se desarrollan y operan en el proyecto independiente `/root/proyect/naperu-mail/source`; para mantenerlo leer sus AGENTS y skill propios. No introducir sus recetas o pruebas en Acrópolis. El guard local `scripts/smtp-network.py` inspecciona sólo el contrato del cliente y no opera el servidor. Mantener explícito `IDENTITY_EMAIL_ENABLED=false` hasta completar DNS/PTR/TLS y entrega controlada. Credenciales generadas privadas en el VPS, nunca por chat. Mientras el modo sea false, registro/reenvío/recuperación quedan deshabilitados. Consultar capabilities y el manifiesto activo para conocer la disponibilidad vigente; no volver a deshabilitar una integración validada sólo por leer este procedimiento de preparación. No auto-confirmar usuarios ni sembrar cuentas QA para crear un administrador. La cuenta inicial debe estar confirmada y su autoridad designada.

Antes de publicar identidad por primera vez o cuando cambie el proxy, ejecutar `python3 scripts/identity-runtime.py --prepare` y `--check`. Preparan PFX/key ring/proxies exactos; no reemplazar automáticamente un protector existente o perdido. Conservar permisos privados y lectura por app UID/GID1654. El preflight exige claves/proxy aun sin SMTP; smtp-check sólo se ejecuta con correo habilitado. Al retomar SMTP validar TLS/autenticación y entrega real controlada.

MFA es obligatorio para Users.Manage, Content.Manage y Subscriptions.Manage; leer `docs/mfa-operations.md` y `docs/catalog-operations.md`. Permisos editoriales, administrativos, niveles y suscripciones son independientes. No publicar el prototipo `dist-preview` como sistema real.

El propietario protegido se aplica con bootstrap explícito auditado, dirección configurada privada y cuenta activa confirmada. Un despliegue de código no confirma correo ni configura MFA. No repetir bootstrap o recuperación sin revisar su estado; la recuperación de backup invalida propiedad, permisos, MFA y suscripciones activas. Leer docs/subscriptions-operations.md.

## Publicación

Comprobar A/AAAA públicos con dos resolutores frente al destino privado esperado; no declarar publicación por `--resolve`. Si DNS falla, conservar routing previo y evitar nuevas solicitudes ACME repetidas; desarrollo puede continuar. No cambiar DNS por inferencia.

Traefik comparte provider file watch y Docker Swarm. Publicar Compose normal por el archivo propio `/etc/dokploy/traefik/dynamic/acropolis-channel.yml`, basado en `infra/traefik/acropolis-channel.yml`. Usar web/websecure y letsencrypt existentes, comprobar alias/red, YAML y colisiones. Respaldar fuera del directorio observado y reemplazar atómicamente. No tocar configuración global, ACME compartido, otros routers ni reiniciar Traefik.

Ejecutar `bash scripts/deploy.sh --expected-sha SHA_VALIDADO`. Sin reconstruir, guarda recuperación/backup, migra y cambia imagen. Para una ejecución larga por SSH conservar un proceso independiente, log privado y código final; comprobar resultado real antes de volver a lanzarlo.

El respaldo completo reúne base, key ring, protector PFX y configuración. En el primer despliegue de identidad, el runtime previo puede no tener key ring: conservar además un respaldo del estado publicado con sus claves recién creadas. Verificar permisos privados sin imprimir material.

## Comprobación y cierre

- Verificar readiness interno, manifiesto/ID de imagen publicado y cuatro historias EF contra las esperadas. Confirmar DNS/TLS normales, HTTP hacia HTTPS, portada/saludo/health/readiness, capabilities false si correo sigue pospuesto, seis categorías/listado y rechazo anónimo de rutas protegidas. Revisar assets/cabeceras y navegador escritorio/móvil cuando sea accesible.
- Un catálogo vacío es válido hasta cargar contenido real; no introducir demos en producción. Conservar datos existentes y distinguir disponibilidad de interfaz de reproducción YouTube/lecturas disponibles según contenido autorizado frente a pagos/AWS todavía pendientes.
- Si la herramienta de navegador niega acceso por una política administrativa, no eludir el bloqueo con otro driver. Registrar la inspección visual pública como no realizada y reportar por separado HTTP/API/TLS y QA; no atribuirlo a un fallo del certificado sin evidencia.
- Confirmar preservación de contenedores/rutas ajenos, salud de web/DB y Git. Guardar verificación sin secretos en `.local/deployments/<run>/` y actualizar `.local/vps-deployment.md`, `.local/continuation-current.md` y el punto local de continuación si está disponible. No fijar un nuevo SHA desplegado sólo porque se publicó documentación.

## Recuperación

Conservar recuperación de imagen/env/Compose/routing y los manifiestos en `.local/deployments/`, respaldos en `.local/backups/`. Las migraciones pueden haber aplicado cambios aunque el runner termine con error: no basta reiniciar una imagen anterior. La receta sólo declara `failed_recovery_applied` después de verificar el ID/estado de esa imagen y su readiness real contra la base actual, usando su snapshot anterior de env/Compose. La configuración privada preparada antes del intento se conserva para retomarlo; no confundirla con la configuración del runtime recuperado.

Si la imagen anterior no acepta el esquema, no existe o su recuperación no se puede comprobar, la receta intenta detener únicamente `web` del proyecto, registra `recovery_incomplete` y retira `last-active-deployment`. Conservar `last-deployment` como historia de la última publicación verificada; el resultado incluye por separado si la parada se confirmó o falló. No afirmar disponibilidad ni reabrir tráfico hasta resolver el mantenimiento. Conservar snapshots/backups y no bajar migraciones, restaurar la base automáticamente ni purgar Docker/down-v de producción.

Restaurar una base antigua requiere web detenido y el procedimiento de invalidación/revalidación de Identity/MFA; no reactivar sesiones, permisos, contraseñas o autenticadores revocados desde el backup. Una imagen anterior a MFA que sirva Identity puede debilitar el acceso aunque acepte SQL: usar una recuperación que conserve MFA o mantenga identidad cerrada. Los helpers de restore de QA nunca se usan sobre producción.
