# Diseño de Acrópolis Channel

## Dirección vigente

Mediateca cultural contemporánea: hermosa, sobria, cercana a jóvenes y práctica. Mantener la identidad de Nueva Acrópolis y la estructura de contenidos de acropolischannel.pe. Aprendemos Juntos y DocPlus sirven como inspiración de jerarquía y descubrimiento, no como plantillas a copiar.

La dirección anterior de bustos, hero editorial repetido y frases decorativas queda sustituida por esta. Aplicar .agents/skills/acropolis-design/SKILL.md a futuros cambios.

## Sistema visual

| Uso | Valor |
| --- | --- |
| Fondo | #F5F8F6 |
| Panel | #FFFFFF |
| Texto | #26343D |
| Marca y enlace | #126CA5 |
| Azul profundo | #183B4B |
| Laurel | #4F725C |
| Interfaz | Source Sans 3, fallback sans-serif |
| Títulos culturales y lecturas | Literata, fallback serif |

Fuentes WOFF2 servidas localmente con sus licencias. Cargar sólo pesos necesarios; interfaz legible, títulos proporcionados, líneas de lectura cómodas. Textura discreta únicamente en superficies culturales; paneles administrativos limpios. Usar sombras/bordes para jerarquía real, no para convertir todo en tarjetas idénticas. Los tokens CSS son la implementación de esta tabla.

## Superficies

- Inicio y explorar: contenido primero, formatos distinguibles, búsqueda/filtros y metadatos útiles. Vacíos honestos; no sembrar producción con ejemplos.
- Ficha: sinopsis pública, acceso claro y enlaces relacionados/ordenados reales. Lecturas completas separadas, texto seguro. YouTube oficial sólo después de pulsar reproducir; controles nativos, mensajes si no permite reproducción y opción de abrir el origen público.
- Cuenta: registro/confirmación/recuperación y MFA conservan su seguridad y estados. Después del registro no se vuelve a pedir el correo; se mantiene únicamente en history.state, sin URLs o almacenamiento adicional.
- Suscripción: mostrar las condiciones de los planes confirmados: Gratuito (`free_beta`) sin vencimiento, sólo para obras marcadas gratuitas; Probacionismo de tres meses naturales y Anual de un año. Un gestor con permiso y MFA indica el inicio y asigna o renueva los planes temporales de forma manual y auditada. La pantalla distingue los estados programado, activo, vencido, cancelado y suspendido, y muestra las fechas reales. La renovación contigua conserva el periodo vigente y extiende su fin, sin cobros ni renovación automática. La activación propia no altera un plan manual ni evita una suspensión administrativa. No inventar precios o contratación; los pagos quedan para después.
- Backoffice: shell separado, navegación por permiso, tablas/filtros, edición con versión, permisos delegados sólo por propietario y auditoría de sólo lectura. Sin hero cultural ni frases en cada formulario.

## Veracidad y accesibilidad

Conservar Lecturas, Documentales, Videos, Podcast, Charlas online y Cursos. Los programas agrupan cursos; no son una séptima categoría principal. Las obras, autores, cifras y fotografías institucionales proceden de datos/materiales autorizados. No afirmar exclusividad de YouTube público ni disponibilidad AWS todavía.

Revisar teclado, foco, contrastes, zoom, ancho móvil, estado vacío, error y carga. Los bordes necesarios para identificar campos activos deben alcanzar al menos 3:1 con el fondo, sin redondear resultados inferiores; ver [W3C, contraste no textual](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html). Los iconos no sustituyen labels y el color no sustituye el texto de estado. La eficiencia incluye fuentes locales, medios diferidos, paginación y bundles razonables. Conservar errores accesibles, reintentos explícitos y prevención de pérdida de cambios. Esta protección incluye Atrás/Adelante dentro de la aplicación y cierre/recarga del navegador; una respuesta tardía no debe cambiar la ruta ya elegida por la persona.

Las referencias de diseño revisadas están fijadas en la skill, sin instaladores ni hooks automáticos. QA visual se realiza contra el candidato aislado; una restricción del navegador público debe declararse, no eludirse.
