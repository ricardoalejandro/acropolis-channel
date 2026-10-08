# Recorridos del gestor: complemento práctico

Guía práctica de los flujos implementados. No acredita QA terminada, despliegue ni disponibilidad en producción. Utilizarla después del gate y en el entorno autorizado; no ejecutar estos recorridos sobre datos reales para probarlos. Complementa [aceptación funcional](functional-acceptance.md), sin sustituir sus controles.

## 1. Entrar con la autoridad correcta

La cuenta debe estar activa y confirmada. Ingresar y completar MFA; la configuración personal está en `/profile/security`. Guardar los códigos de recuperación privadamente, sin incluirlos en capturas o informes.

| Trabajo | Permiso y ruta |
| --- | --- |
| Editar/publicar y mantener Temas | `Content.Manage`; `/admin/content` y `/admin/topics` |
| Gestionar cuentas | `Users.Manage`; `/admin/users` |
| Gestionar planes y sus movimientos | `Subscriptions.Manage`; `/admin/subscriptions` y `/admin/reports/subscription-events` |
| Consultar consumo general | `Content.Manage`; `/admin/reports/consumption` |
| Consultar consumo individual | `Content.Manage` **y** `Users.Manage`; `/admin/users/:id/consumption` |

Estos permisos son independientes de los niveles institucionales. Sólo el propietario delega: abrir una cuenta distinta de la propia cuenta protegida en `/admin/users/:id`, seleccionar **Permisos administrativos** y pulsar **Guardar permisos**. Los cambios revocan las sesiones afectadas: la persona vuelve a ingresar y completa MFA. El propietario tampoco omite MFA. Sin permiso, detener el recorrido y recurrir al propietario; no cambiar niveles para buscar acceso. Referencias: [identidad](identity-operations.md) y [MFA](mfa-operations.md).

## 2. Preparar una obra y publicarla

1. Abrir `/admin/content/new`. Completar título, formato, resumen y sinopsis; éstos son públicos. Para una lectura, colocar el texto completo en **Lectura completa**; para vídeo, usar **Enlace o identificador de YouTube**. No poner la obra restringida en la sinopsis.
2. Decidir explícitamente **Disponible con el plan Gratuito**. Está desmarcado por defecto y afecta sólo a esta obra; un curso gratuito no libera a sus elementos.
3. Pulsar **Guardar borrador**. Abrir la ficha guardada, revisar el material y, cuando esté listo, **Publicar contenido** → **Confirmar publicación**. Comprobar la ficha `/content/:slug` con visitante y con las cuentas de QA que tengan el acceso correspondiente.
4. Para ocultarla, usar **Retirar publicación** o **Archivar contenido** y confirmar la acción. Una ficha archivada puede **Volver a borrador**. La dirección publicada se conserva; no hay borrado físico desde la aplicación.

Un enlace válido no garantiza reproducción de YouTube. Si el proveedor impide reproducir, registrar la limitación real y usar el enlace de origen que ofrece la ficha; no considerar una captura prueba de reproducción. Ver [catálogo y obras](catalog-operations.md) para formatos, cursos y programas.

## 3. Mantener Temas y sus asociaciones

En `/admin/topics/new`, escribir el nombre y **Guardar tema**. Abrir un tema para renombrarlo, **Archivar tema** o **Restaurar tema**. Para ordenar desde `/admin/topics`, dejar búsqueda y estado sin filtros y usar **Subir** o **Bajar**.

En la ficha, abrir **Gestionar temas del contenido** (`/admin/content/:id/topics`), seleccionar hasta doce y **Guardar temas del contenido**. Archivar un tema conserva las asociaciones existentes; no permite nuevas. Comprobar en `/explore` la combinación de tema, formato y búsqueda. Detalles en [Temas](catalog-topics.md).

## 4. Asignar o renovar un plan manual

1. Abrir `/admin/subscriptions`, localizar al titular con el selector de cuenta y entrar en **Asignar o renovar plan de esta cuenta**. La cuenta destino debe estar activa y confirmada. Una suscripción existente permite **Gestionar suscripción**.
2. Elegir Gratuito, Probacionismo o Anual. Gratuito comienza inmediatamente y no solicita fecha. Los otros planes piden **Inicio del período (hora de Lima)**: tres meses naturales o un año desde ese inicio. Registrar el motivo real, **Revisar asignación** y **Confirmar asignación**.
3. Para prolongar el mismo plan, usar **Renovar plan actual** → **Revisar renovación** → **Confirmar asignación**. El nuevo periodo parte del fin vigente; el servidor conserva el inicio del acceso actual y extiende el fin. No sustituirlo manualmente por una fecha futura que recorte acceso vigente.
4. Revisar las fechas devueltas y distinguir estado registrado de estado efectivo: un plan puede figurar activo pero estar programado o vencido. **Guardar estado** cambia el estado con motivo; reactivar no extiende fechas. El titular consulta `/profile/subscription`.

No hay cobros ni renovación automática. Un plan manual vencido requiere revisión del gestor; la activación personal gratuita no lo sustituye. Ver [suscripciones](subscriptions-operations.md).

## 5. Leer los reportes sin inventar cifras

En `/admin/reports` consultar los paneles permitidos: estado actual no equivale a un historial de saldos. En `/admin/reports/subscription-events`, las fechas son UTC y **Hasta (incluido)** incluye el último día elegido; los eventos son movimientos registrados, no ingresos ni personas únicas.

En `/admin/reports/consumption`, elegir **Desde (UTC)** y **Hasta (sin incluir, UTC)**; pulsar **Consultar consumo**. El detalle por cuenta conserva 90 fechas UTC incluida hoy; las estadísticas generales, 365. **No disponible** no significa cero: falta detalle para ese intervalo. El tiempo observado puede solaparse y no acredita atención o finalización. Si falla, **Reintentar consulta** conserva la consulta intentada. Ver [actividad registrada](consumption-activity-operations.md).

## 6. Resolver un conflicto y preparar el piloto

Un conflicto de versión significa que otra operación cambió la fila. Conservar el trabajo, revisar el estado actual y usar la recarga ofrecida sólo tras aceptar que sustituirá el formulario; volver a decidir el cambio. No repetir a ciegas una renovación ni modificar versiones manualmente. Una respuesta 429 requiere respetar el límite; un error 503 o de red no confirma que la operación se haya guardado.

Para la aceptación manual, el operador prepara un entorno QA aislado con propietario, gestores de permisos separados, titular Gratuito y titular temporal, todos sintéticos. Crear una lectura de QA gratuita y otra restringida; recorrer publicación/Temas, accesos, asignación y renovación. Comprobar teclado y móvil, conflictos, ausencia de duplicados y reportes. Registrar SHA, recorrido y resultado pendiente/observado; no guardar contraseñas, tokens, códigos MFA o datos de personas reales. Seguir la [matriz existente](functional-acceptance.md).

Los avisos al titular por asignación manual, renovación y ventana previa al vencimiento están **deshabilitados por defecto**. El modo apagado tampoco encola. Esta guía no cambia flags ni configura SMTP productivo. Sus pruebas de envío corresponden al entorno sintético con SMTP/CA de QA, habilitado explícitamente por el operador autorizado; no reenviar correos reales ni inferir entrega en bandeja por aceptación SMTP. Activación, recuperación y ledger se rigen por [operación de avisos](subscriptions-operations.md#avisos-transaccionales-al-titular). Las comprobaciones del piloto no certifican por sí solas el sistema ni sustituyen el gate.
