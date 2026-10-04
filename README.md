# Acrópolis Channel

Monolito modular con ASP.NET Core / .NET 10 LTS, React 19, TypeScript, Vite y PostgreSQL 18. El código y las validaciones se trabajan en el VPS; la distribución multimedia futura estará en AWS.

## Trabajo y alcance

Checkout canónico: `/root/proyect/acropolis-channel`, rama `main`, remoto HTTPS `https://github.com/ricardoalejandro/acropolis-channel`. La carpeta Windows guarda instrucciones y skills, nunca otra copia del código.

```powershell
ssh -o BatchMode=yes -o StrictHostKeyChecking=yes vps
```

La fase de identidad incorpora registro con confirmación de correo, acceso, recuperación y cambio de contraseña, perfil y administración de usuarios. Los niveles institucionales pueden coexistir y se gestionan por separado del permiso administrativo `Users.Manage`. El correo utiliza el SMTP existente, configurado privadamente.

La portada tiene una dirección editorial inspirada en Acrópolis Channel Perú. El prototipo del catálogo se construye aparte: no contiene reproducción, pagos ni suscripciones operativas. Los precios y condiciones del sitio anterior son referencias pendientes de validación comercial. No se migra WordPress ni se crean recursos AWS en esta fase. MFA queda pendiente antes del lanzamiento operativo.

- [Arquitectura y límites](docs/architecture.md).
- [Diseño, estructura y procedencia de imágenes](docs/design.md).
- [Identidad: correo, administración y recuperación](docs/identity-operations.md).
- [Validación y criterios de calidad](docs/quality.md).

## Contratos y aplicación

React y API comparten origen. Las API desconocidas devuelven 404, sin ocultarse tras el fallback de la SPA. El sitio requiere HTTPS para las cookies de cuentas.

| Ruta | Responsabilidad |
| --- | --- |
| `/api/v1/identity/csrf` | Token antifalsificación para operaciones que modifican estado |
| `/api/v1/identity/*` | Registro, confirmación, sesión, recuperación y perfil |
| `/api/v1/admin/users` | Búsqueda, filtros y gestión paginada con autorización |
| `/api/v1/greeting` | Contrato de diagnóstico `{"message":"Hola mundo"}` |
| `/health` | Liveness sin dependencia de PostgreSQL |
| `/health/ready` | Conexión y migraciones esperadas de Platform e Identity |

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

Seguir [la operación de identidad](docs/identity-operations.md) para configurar SMTP, preparar el protector cifrado y registrar las direcciones exactas de Traefik. El remitente real y el primer administrador deben quedar definidos antes de habilitar cuentas.

Sólo cuando el despliegue esté solicitado:

```bash
bash scripts/deploy.sh --expected-sha SHA_VALIDADO
```

El script exige main limpio, coincidencia con origin y QA aprobado para las imágenes exactas; comprueba configuración y conexión SMTP, respalda datos y material de identidad, ejecuta migraciones y cambia la aplicación. No aplica migraciones descendentes ni reconstruye.

Compose mantiene el proyecto `acropolis-channel`, web interna 8080, alias `acropolis-channel-web` y red externa `dokploy-network`. PostgreSQL tiene una red privada y roles separados para administración, migración y aplicación. Sólo se modifica el routing propio `/etc/dokploy/traefik/dynamic/acropolis-channel.yml`; se preservan Traefik global y los demás proyectos.

Verificar DNS público, certificado válido, redirección HTTP, interfaz, API y entrega real del correo. Un build o una prueba interna no acreditan publicación. La preparación de código o QA no autoriza desplegar.

## Respaldo y capacidad

Los manifiestos se guardan en `.local/deployments/` y las copias en `.local/backups/`. Una copia completa incluye base, key ring, protector y configuración privada. Tras restaurar una base antigua, poner cuentas en mantenimiento y revalidar su seguridad según el procedimiento; nunca reabrir sesiones o credenciales revocadas por restaurar un backup.

`restore-db-test.sh` sólo admite destinos QA `acropolis_test_*`. No borrar volúmenes productivos ni restaurar producción como operación rutinaria.

El objetivo del producto es 100.000 cuentas y alrededor de 1.000 usuarios simultáneos. Sembrar 100.000 registros y probar concurrencia acotada permite detectar problemas iniciales, pero no acredita todavía esa capacidad operativa.
