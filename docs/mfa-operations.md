# Verificación en dos pasos

MFA usa TOTP de ASP.NET Core Identity y una aplicación autenticadora compatible. No envía códigos por correo o SMS y no depende de SMTP. El registro y la recuperación de contraseña siguen sus propios requisitos de confirmación: deshabilitar correo no elimina esas comprobaciones.

## Acceso y enrolamiento

Una cuenta sin permisos administrativos puede activar MFA desde su perfil, tras verificar su contraseña actual. Las cuentas con `Users.Manage` o `Content.Manage` deben enrolarse antes de obtener una sesión administrativa. Los niveles institucionales no conceden permisos ni eximen del segundo factor.

`POST /api/v1/identity/login` comprueba primero contraseña, confirmación, estado y bloqueo. Para una cuenta que requiere MFA devuelve HTTP 202 con `mfaRequired`, `enrollmentRequired`, un `challengeToken` y `expiresUtc`; todavía no emite una sesión plena. El desafío aleatorio sólo se almacena como hash, vinculado a la cuenta y su versión de seguridad, y caduca en cinco minutos. Crear otro desafío invalida los anteriores de esa cuenta.

Si se requiere enrolamiento, `POST /mfa/enrollment` recibe el desafío limitado. Para una cuenta ya autenticada recibe únicamente `currentPassword`. Devuelve una nueva clave manual y URI `otpauth`, exclusivamente durante ese enrolamiento. La clave queda cifrada con Data Protection y no se expone después de activarla. `POST /mfa/enable` exige un código correcto generado con esa clave; sólo entonces activa MFA, revoca las sesiones anteriores y emite una nueva cookie segura con `amr=mfa`.

Una cuenta ya enrolada termina el acceso mediante `POST /mfa/challenge`, enviando el desafío y exactamente uno de `code` o `recoveryCode`. El proveedor oficial valida los códigos TOTP de seis dígitos, período de treinta segundos y su ventana de tolerancia. La aplicación añade una huella HMAC de consumo durante tres minutos para impedir la reutilización, incluso entre desafíos concurrentes. Si un código ya se utilizó, esperar al siguiente que muestre la aplicación autenticadora.

Una reautenticación reemplaza también la referencia aleatoria de sesión: una copia de la cookie previa permanece inválida y no adquiere el segundo factor ni la identidad nueva. Las transacciones de cambios administrativos, contraseña, enlaces y MFA comparten el bloqueo por usuario antes de modificar filas y revocar sesiones.

Todos estos endpoints están bajo antifalsificación, validación de origen y el límite de treinta peticiones por minuto por IP. Cinco pruebas incorrectas bloquean la cuenta para operaciones MFA durante quince minutos mediante un contador persistido; crear otro desafío no reinicia ese bloqueo. Un desafío consumido, caducado o con cinco intentos se rechaza. Las políticas administrativas exigen un segundo factor acreditado; un permiso en una cookie obtenida únicamente con contraseña no basta.

## Recuperación y cambios

La activación devuelve diez códigos de recuperación aleatorios de 128 bits. Deben guardarse en un lugar privado: se muestran una sola vez, se persisten únicamente como hashes y cada código se consume de forma atómica. No se registran códigos, claves ni tokens en auditoría. No existe la opción de recordar un equipo ni una exención administrativa.

`GET /mfa` informa únicamente si MFA está activo, si es obligatorio y cuántos códigos quedan. `POST /mfa/recovery-codes` exige contraseña actual y segundo factor; sustituye todos los códigos anteriores, devuelve los nuevos y revoca todas las sesiones, incluida la cookie actual. `POST /mfa/disable` exige la misma reautenticación y revoca sesiones. Una cuenta que conserva permisos administrativos recibe HTTP 409 `mfa_required_for_admin` y no puede desactivar MFA.

Perder el autenticador y todos los códigos no habilita un endpoint de omisión ni un reset por correo. Debe intervenir un operador autorizado mediante el procedimiento de recuperación en mantenimiento, con revalidación explícita. Nunca conceder permisos ni registrar contraseñas fijas para eludir el segundo factor. El bootstrap administrativo concede el permiso a una cuenta exacta, confirmada y activa; su siguiente acceso exige enrolamiento TOTP.

## Persistencia y restauración

Los datos pertenecen al schema Identity: credenciales cifradas, desafíos, hashes de recuperación y huellas de consumo. La clave de cifrado depende del key ring y protector persistentes de Data Protection. Respaldar base, key ring, protector y configuración como un conjunto privado; reiniciar la aplicación no debe perder la clave ni aceptar un código ya consumido.

Restaurar una base antigua también puede restaurar permisos, contraseñas y claves de autenticador antiguos. Antes de abrir la aplicación, `recovery-invalidate --maintenance` elimina sesiones y todo el material MFA restaurado, invalida enlaces y correos y pone las cuentas en cuarentena. `recovery-revalidate` retira permisos administrativos y niveles anteriores y exige contraseña y confirmación nuevas; `recover-admin` conserva únicamente la autorización administrativa explícita del operador y también exige credenciales nuevas y un enrolamiento MFA nuevo. Los códigos y autenticadores del backup no vuelven a ser válidos. Una imagen anterior a MFA que ya implementara cuentas podría omitir el segundo factor aunque entienda el esquema: no usarla para recuperar el acceso de cuentas. La versión de recuperación debe conservar MFA o mantener los endpoints de identidad fuera de servicio; la compatibilidad SQL por sí sola no acredita compatibilidad de seguridad.

`prune-identity` elimina en lotes de hasta mil los desafíos MFA consumidos o caducados y las huellas de replay caducadas; conserva las credenciales y códigos de recuperación activos. Ejecutarlo con la misma protección operativa del resto de comandos manuales de Identity.

## Verificación

Las pruebas deben ejercitar HTTP y PostgreSQL real: acceso sin cookie plena, confirmación TOTP previa a activación, secreto cifrado, CSRF y origen, códigos incorrectos, caducidad, bloqueo persistente, carreras de consumo, regeneración, revocación, administración obligatoria, ocho horas absolutas, limpieza y recuperación. QA usa claves y cuentas sintéticas propias, certificados HTTPS confiados y artefactos privados. Generar un TOTP en un helper de pruebas no reemplaza el proveedor oficial productivo ni permite desactivar MFA en el candidato.

Referencias: [MFA de ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/mfa?view=aspnetcore-10.0) y [proveedor oficial TOTP](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Identity/Extensions.Core/src/AuthenticatorTokenProvider.cs).
