# Acrópolis Channel

Monolito modular con ASP.NET Core / .NET 10 LTS, React 19, TypeScript, Vite y PostgreSQL 18. El código y las validaciones se trabajan en el VPS; el piloto reproduce YouTube oficial y sirve lecturas completas. La integración de archivos en AWS se incorporará después.

## Trabajo y alcance

Checkout canónico: `/root/proyect/acropolis-channel`, rama `main`, remoto HTTPS `https://github.com/ricardoalejandro/acropolis-channel`. La carpeta Windows guarda instrucciones y skills, nunca otra copia del código.

```powershell
ssh -o BatchMode=yes -o StrictHostKeyChecking=yes vps
```

La fase de identidad incorpora registro con confirmación de correo, acceso, recuperación y cambio de contraseña, perfil y administración de usuarios. Los niveles institucionales pueden coexistir y se gestionan por separado del permiso administrativo `Users.Manage`. El correo puede permanecer deshabilitado explícitamente mientras su integración está pospuesta. Al habilitarlo utiliza SMTP autenticado y TLS, configurado privadamente.

La mediateca tiene seis categorías, búsqueda, filtros, paginación y fichas públicas, con una dirección visual propia documentada y fuentes locales. Las obras completas están separadas de sus sinopsis y requieren cuenta activa confirmada y suscripción gratuita explícita. La implementación WIP ofrece un piloto gratuito sin vencimiento. Este piloto no sustituye los tipos, fechas y renovaciones del documento funcional, todavía pendientes de confirmar. YouTube conserva sus controles y la disponibilidad de su origen público; no se presenta como multimedia privada exclusiva.

En la gestión editorial se podrá pegar un enlace HTTPS de YouTube o su identificador de once caracteres. La guía de [uso del catálogo](docs/catalog-operations.md#añadir-un-vídeo) explica los formatos admitidos, cómo guardar el borrador y publicar. La reproducción depende de la disponibilidad de YouTube; AWS será una modalidad futura, sin recursos creados en esta preparación.

El backoffice tiene navegación propia y permisos separados Users.Manage, Content.Manage y Subscriptions.Manage, todos con MFA. El propietario protegido se designa mediante un CLI auditado y delega autoridad; los demás gestores no pueden suspenderlo ni quitarle permisos. Se gestionan cuentas/niveles, contenidos/publicación, cursos/programas ordenados, suscripciones y auditoría de sólo lectura. Los pagos quedan para después. La facturación electrónica solicitada sigue pendiente de identificar e integrar el mecanismo existente; la gratuidad del piloto no la excluye automáticamente. La integración AWS real y la migración WordPress siguen pendientes.

- [Arquitectura y límites](docs/architecture.md).
- [Diseño, estructura y procedencia de imágenes](docs/design.md).
- [Identidad: correo, administración y recuperación](docs/identity-operations.md).
- [Catálogo editorial y pendientes funcionales](docs/catalog-operations.md).
- [Suscripción gratuita y consumo](docs/subscriptions-operations.md).
- [Alcance de la entrega](docs/modernization-scope.md).
- [MFA y recuperación](docs/mfa-operations.md).
- [Validación y criterios de calidad](docs/quality.md).

## Contratos y aplicación

React y API comparten origen. Las API desconocidas devuelven 404, sin ocultarse tras el fallback de la SPA. El sitio requiere HTTPS para las cookies de cuentas.

| Ruta | Responsabilidad |
| --- | --- |
| `/api/v1/identity/csrf` | Token antifalsificación para operaciones que modifican estado |
| `/api/v1/identity/*` | Registro, confirmación, sesión, recuperación y perfil |
| `/api/v1/catalog/categories` | Seis categorías oficiales |
| `/api/v1/catalog/content` | Listado paginado y fichas de contenidos publicados |
| `/api/v1/admin/content` | Gestión editorial y auditoría con Content.Manage y MFA |
| `/api/v1/consumption/content/{slug}` | Obra publicada para cuenta confirmada activa y suscripción activa |
| `/api/v1/subscriptions/*` | Estado, activación gratuita explícita y cancelación propias |
| `/api/v1/admin/subscriptions` | Administración y auditoría con Subscriptions.Manage y MFA |
| `/api/v1/identity/capabilities` | Disponibilidad explícita del correo, sin secretos |
| `/api/v1/admin/users` | Búsqueda, filtros y gestión paginada con autorización |
| `/api/v1/greeting` | Contrato de diagnóstico `{"message":"Hola mundo"}` |
| `/health` | Liveness sin dependencia de PostgreSQL |
| `/health/ready` | Conexión y migraciones esperadas de Platform, Identity, Catalog y Subscriptions |

El servidor guarda sesiones revocables y aplica un máximo absoluto de ocho horas. Los enlaces de correo se consumen una sola vez; registro y recuperación evitan revelar si una cuenta existe. La administración aplica control de concurrencia y auditoría. Los niveles no conceden permisos administrativos por sí mismos.

## Desarrollo y QA

No hay GitHub Actions. Node.js 22 y SDK .NET 10 se usan dentro de Docker; se conserva Node.js del host. Todo cambio se valida en recursos `acropolis_test_*`, PostgreSQL real, correo de pruebas y HTTPS con una CA exclusiva de QA. Nunca se usa la configuración ni los datos de producción.

```bash
cd /root/proyect/acropolis-channel
git status --short --branch
git fetch origin
# Sincronizar por fast-forward antes de editar cuando el estado lo permita.
bash scripts/verify.sh --working-tree
# Revisar y crear el commit completo; después certificar el checkout limpio:
bash scripts/verify.sh
# Publicar sólo si el gate completo pasa:
git push origin main
```

El modo `--working-tree` permite iterar, pero nunca certifica un despliegue. Informes y capturas permanecen en `.local/qa/<sha>/<run>/`. Las imágenes finales se identifican por SHA y por su identificador Docker: publicar no las reconstruye.

El prototipo usa `npm run build:preview` y genera `frontend/dist-preview/`, aislado del build productivo. Es material de evaluación visual con contenido de demostración, no un catálogo disponible para usuarios reales.

## Configuración y despliegue manual

La URL objetivo es **https://acropolischannel.naperu.cloud**. Configurar `.env` exclusivamente en el VPS, a partir de `.env.example`, con permisos 600. Nunca versionar secretos, dumps, reportes privados, el protector PFX ni el key ring.

Seguir [la operación de identidad](docs/identity-operations.md) para preparar el protector cifrado y las direcciones exactas de Traefik. Con IDENTITY_EMAIL_ENABLED=false, el catálogo público puede funcionar sin SMTP; registro, reenvío y solicitud de recuperación quedan deshabilitados, sin crear cuentas automáticamente confirmadas. Las cuentas ya confirmadas conservan acceso sujeto a MFA cuando corresponde. Para habilitar el correo se necesita configurar y comprobar el SMTP real y su remitente.

Sólo cuando el despliegue esté solicitado:

```bash
bash scripts/deploy.sh --expected-sha SHA_VALIDADO
```

El script exige main limpio, coincidencia con origin y QA aprobado para las imágenes exactas; comprueba configuración y, cuando el correo está habilitado, conexión SMTP, respalda datos y material de identidad, ejecuta migraciones y cambia la aplicación. No aplica migraciones descendentes ni reconstruye.

Compose mantiene el proyecto `acropolis-channel`, web interna 8080, alias `acropolis-channel-web` y red externa `dokploy-network`. PostgreSQL tiene una red privada y roles separados para administración, migración y aplicación. Sólo se modifica el routing propio `/etc/dokploy/traefik/dynamic/acropolis-channel.yml`; se preservan Traefik global y los demás proyectos.

Verificar DNS público, certificado válido, redirección HTTP, interfaz, API y, si se habilitó el correo, su entrega real. Un build o una prueba interna no acreditan publicación. La preparación de código o QA no autoriza desplegar.

## Respaldo y capacidad

Los manifiestos se guardan en `.local/deployments/` y las copias en `.local/backups/`. Una copia completa incluye base, key ring, protector y configuración privada. La recuperación de una imagen anterior exige comprobar su ID y readiness frente al esquema actual, incluso si la migración falló parcialmente. Sólo entonces se registra `failed_recovery_applied`; si falla la comprobación, queda `recovery_incomplete`, se intenta detener sólo web y se retira el puntero activo. Los backups y la última publicación verificada se conservan; no se ejecutan migraciones descendentes ni restauraciones automáticas. La configuración privada preparada se conserva y el runtime recuperado usa su snapshot anterior por separado. Tras restaurar una base antigua, poner cuentas en mantenimiento y revalidar su seguridad según el procedimiento; nunca reabrir sesiones o credenciales revocadas por restaurar un backup.

`restore-db-test.sh` sólo admite destinos QA `acropolis_test_*`. No borrar volúmenes productivos ni restaurar producción como operación rutinaria.

El objetivo del producto es soportar 100.000 cuentas y alrededor de 1.000 usuarios simultáneos. La aplicación conserva las cuentas reales existentes y no precarga usuarios. Las 100.000 cuentas sintéticas se crean únicamente en bases temporales de QA y se eliminan al cerrar las pruebas; esa carga acotada permite detectar problemas iniciales, pero no acredita todavía toda la capacidad operativa.
