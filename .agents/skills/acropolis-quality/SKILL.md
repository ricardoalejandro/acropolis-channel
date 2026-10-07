---
name: acropolis-quality
description: "Desarrollar y validar Acropolis Channel en el VPS con pruebas por nivel, PostgreSQL real y QA aislado. Usar para programación, revisión y entrega del proyecto."
---

# Calidad de Acropolis Channel

## Contexto y alcance

Leer `AGENTS.md`, `docs/architecture.md`, `docs/quality.md` y el documento del módulo en `/root/proyect/acropolis-channel`. Desde Windows usar SSH estricto con alias vps; si ya se trabaja en el VPS, operar directamente. Las rutas de esta skill son del checkout canónico; no guardar código en Windows.

Retomar desde `.local/continuation-current.md` y comprobar Git/runtime. Conservar trabajo existente y sincronizar ff-only cuando sea compatible. Las instrucciones locales sólo cargan esta versión canónica; actualizar reglas completas aquí.

Backend modular .NET 10/EF 10/Npgsql, React 19/TypeScript/Vite, PostgreSQL 18. Usar SDK 10/Node 22 en Docker y conservar Node 20 host. Los módulos actuales son Platform, Identity, Catalog y Subscriptions; no añadir módulos de negocio sin contrato real. La mediateca incluye YouTube oficial y lecturas completas. Gratuito permite sólo obras marcadas gratuitas; Probacionismo dura tres meses naturales y Anual un año, con asignación y renovación manual auditada. Leer `docs/modernization-scope.md` para los procesos solicitados y sus pendientes; sólo los pagos están aplazados. No convertir el estado WIP en aprobación de reglas de planes ni en exclusión de facturación, AWS o integración institucional.

## Desarrollo y pruebas

- Respetar propiedad modular y contratos; verificar reglas y límites, casos de uso, persistencia/transacciones, HTTP, autorización, errores y recorrido del usuario. Concurrencia, deduplicación e idempotencia son obligatorias para procesos que lo requieran, especialmente pagos.
- Backend xUnit/WebApplicationFactory; persistencia PostgreSQL real, sin fallback en memoria. Interfaz Vitest/React Testing Library; recorridos Playwright escritorio/móvil y revisión de teclado, contraste, imágenes, cargas y errores.
- Cobertura mínima 80% de líneas/ramas de lógica propia y métricas frontend de docs/quality.md. Comprobar casos críticos aun con buen porcentaje; no omitir tests por falta de base ni inflar cobertura con DTOs/pruebas ficticias.
- Aplicar acropolis-design y preservar las seis categorías oficiales y la dirección vigente de docs/design.md. El contenido y las APIs de producción son reales; estados vacíos son válidos. El prototipo `dist-preview`, multimedia y cobros demostrativos no entran en la aplicación productiva.

## Gate y evidencias

Iterar con `bash scripts/verify.sh --working-tree`; su resultado no habilita despliegue. Para código/scripts/migraciones/configuración de build o runtime, crear el commit definitivo y ejecutar `bash scripts/verify.sh` limpio antes de publicar.

El gate cubre formato/análisis/tipos/lint, auditorías de dependencias, unitarias/cobertura, integración y arquitectura, migraciones, API/componentes/navegador, TLS/configuración Production, resiliencia/reinicio, respaldo/restauración y carga limitada. Fallos bloquean la entrega: corregir su causa sin bajar umbrales ni relajar controles.

Ejecutar una sola suite/carga pesada a la vez en el VPS compartido. Usar sólo proyectos/redes/volúmenes `acropolis_test_*`, datos y credenciales sintéticos, CA/SMTP propios y límites de recursos. Nunca cargar `.env` de producción, sus volúmenes o el socket Docker. Limpiar únicamente recursos de la ejecución, verificando su pertenencia.

Guardar logs privados con secretos redactados y `report.json` en `.local/qa/<sha>/<run>/`. La certificación exige passed, deployment_eligible=true, checkout sin cambios y SHA/IDs exactos de web/migraciones. Conservar imágenes y reportes; no reconstruir una imagen después de certificarla ni considerar una desconexión SSH un éxito. En operaciones largas conservar proceso independiente, log y resultado final para retomar sin duplicarlas.

Para restauración, seguir el comparador de `docs/quality.md`: referencia de sólo esquema del mismo dump y comparación completa antes/después del runner. PostgreSQL puede reescribir CHECK equivalentes; no eliminarlos del digest ni normalizar con reglas que escondan cambios. Mantener historia EF, propietarios/ACL y digest de fichas/auditoría independientes.

Cambios únicamente documentales/de skills requieren validación de contenido, referencias y frontmatter, no una reconstrucción ni cargas de runtime. No presentar su nuevo SHA como certificado si no tiene un informe propio. Una certificación vigente de la versión publicada sigue siendo evidencia de esa versión, no del HEAD documental posterior.

## Identity, Catalog y decisiones vigentes

Leer `docs/identity-operations.md`, `docs/mfa-operations.md` y `docs/catalog-operations.md` cuando esos procesos cambien.

- SMTP propio está autorizado: leer `docs/smtp-integration.md`; no pedir credenciales por chat ni usar las productivas en QA aislada. Probar `IDENTITY_EMAIL_ENABLED=false`, respuestas 503/email_unavailable en emisión, ninguna cuenta nueva/envío y acceso de confirmados. Protector/key ring/HTTPS/proxies exactos siguen obligatorios. Los tests con correo usan sólo SMTP aislado con TLS/CA validada.
- Identity: confirmación/tokens de un uso, lockout, CSRF/origen, ocho horas absolutas, revocación y rotación de sesiones, perfiles, permisos/último administrador y concurrencia. MFA TOTP obligatorio para Users.Manage/Content.Manage/Subscriptions.Manage; verificar desafíos sin sesión plena, caducidad, replay, códigos de recuperación concurrentes y reautenticación. No registrar semillas, URI/QR ni códigos.
- Catalog: categorías, límites, búsqueda literal/paginación estable, borrador/publicación/retirada/archivo, slug estable, conflictos sin pérdida de texto y auditoría atómica. Separar Content.Manage de Users.Manage/niveles. Sinopsis públicas escapadas; borradores/archivados404. Obra/YouTube separados, sin filtración pública; consumo sólo confirmed+active+accesovigente+published. Gratuito sólo autoriza IsFree; comprobar esa marca y la versión dentro de la lectura/start/pulse/replay, sin herencia de acceso desde cursos/programas. Cursos→obras y programas→cursos ordenados sin ciclos, referencias inválidas ni contenido retirado. Auditoría de sólo lectura paginada por permiso.
- Subscriptions: activación gratuita explícita idempotente, un registro por usuario, cancelación/versionado, suspensión no eludible y auditoría. Probar Gratuito limitado, fechas UTC de inicio/fin exclusivo, meses naturales, años bisiestos, estados programado/vencido, asignación y renovación por Subscriptions.Manage+MFA con razón/versión. La renovación contigua conserva el inicio vigente y extiende el fin; rechazar reemplazos futuros que recorten acceso activo y dobles extensiones por reintento. La activación propia no cambia un plan manual ni sus fechas. Probar permisos independientes y concurrencia real. Propietario protegido: bootstrap/recovery auditados, delegación owner-only, prohibición de suspenderlo por otros y ausencia de atajos MFA.
- Avisos transaccionales: leer `docs/subscriptions-operations.md`. Probar asignación/renovación al titular y recordatorio en la ventana de siete días, outbox atómica con auditoría, defaultoff también sin encolado, no-ops/conflictos, cuenta activa/confirmada y términos vigentes. Verificar lote acotado sin starvation, generación estable por periodo, doble worker/scope reutilizado, presupuesto PostgreSQL compartido, lease/reintentos y recuperación con tombstone. Reutilizar transporte SMTP de Identity y conservar sus recorridos; QA usa SMTP/CA sintéticos con validación TLS normal, worker Testing sólo opt-in y ninguna credencial o envío productivo. Aceptación SMTP no prueba llegada ni exactly-once; no borrar el ledger mínimo usando los plazos de consumo.
- Restauración: invalidar propiedad protegida, todos los permisos y acceso restaurado; invalidar cuentas, sesiones, enlaces/outbox y todo material MFA restaurado; revalidar sin recuperar automáticamente permisos. Guardas de producción obligatorias, sin pruebas destructivas sobre datos reales.

## Cierre

Informar sólo resultados comprobados, actualizar continuidad privada y conservar capturas seguras. Las 100.000 cuentas son una meta de capacidad: no precargar producción ni añadir semillas al arranque o migraciones. Los datos sintéticos masivos existen sólo en bases temporales de QA que se eliminan al cerrar. Las mediciones de 100.000 cuentas/10.000 fichas con 50 sesiones/lectores son limitadas; no acreditan 1.000 concurrentes ni reproducción AWS. Si una política impide navegador público, no eludirla y distinguir QA de la inspección pública faltante.

Para un despliegue solicitado aplicar la [skill de despliegue](../acropolis-vps-deploy/SKILL.md). QA y push no autorizan por sí solos cambiar producción.
