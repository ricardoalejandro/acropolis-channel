# Modalidad AWS futura para Acrópolis Channel

Diseño de una modalidad posterior de medios propios, contrastado con documentación oficial el 6 de octubre de 2026. La fase actual utiliza referencias de YouTube; AWS todavía no tiene recursos configurados. Esta guía define la futura integración y sus criterios de aceptación. La implementación y habilitación de AWS requieren un trabajo posterior; no acreditan reproducción, capacidad ni costes actuales.

## Diseño propuesto

Mantener el monolito modular en el VPS: Identity, Subscriptions y Catalog deciden acceso; S3 almacena medios y CloudFront entrega sus bytes. La cuenta debe estar activa y confirmada, la suscripción activa y la obra publicada. Propietario y gestores tampoco reciben bypass. Sinopsis y metadatos públicos no incluirán objetos privados ni permisos de reproducción. Conservar las seis categorías y YouTube como modalidad compatible; no extraer audio de sus vídeos.

Para AWS, separar originales y derivados privados, con bloqueo de acceso público. CloudFront usaría origen S3 normal, OAC con firma always y política limitada a la distribución y objetos de salida; los originales no serían origen público. Object Ownership y cifrado se definirán en infraestructura; SSE-KMS exige permisos adicionales si se adopta. [OAC oficial](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/private-content-restricting-access-to-s3.html), [bloqueo público S3](https://docs.aws.amazon.com/AmazonS3/latest/userguide/access-control-block-public-access.html).

Vídeos: preparar HLS con varias calidades mediante MediaConvert, sin transcodificar en el VPS. Podcasts: audio independiente, inicialmente archivo reproducible con seek/Range; HLS de audio sólo si se necesita. MediaConvert admite paquetes ABR y salidas de audio, pero sus perfiles deben validarse con muestras autorizadas antes de automatizar. [Paquetes ABR](https://docs.aws.amazon.com/mediaconvert/latest/ug/outputs-file-ABR.html), [audio](https://docs.aws.amazon.com/mediaconvert/latest/ug/audio-only.html), [Range](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/RangeGETs.html).

## Autorización CDN y dominio pendiente

Se proponen cookies firmadas CloudFront para el conjunto HLS (manifiestos, segmentos, audio y subtítulos de una sola obra/versión), y URL firmada para un archivo de audio individual. Firmar únicamente el manifiesto no concede acceso a sus segmentos. Las referencias públicas existentes no se convertirán en URLs arbitrarias. [Elección oficial de firma](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/private-content-choosing-signed-urls-cookies.html).

Dominio sugerido, aún no configurado ni aprobado para crear: media.acropolischannel.naperu.cloud. Así el portal podría emitir las tres cookies de reproducción con Domain=acropolischannel.naperu.cloud y Path de la obra, Secure/HttpOnly; la cookie __Host de login seguirá exclusivamente en el portal. No extender cookies a todo naperu.cloud. El POST de concesión debe mantener CSRF y no-store. La caducidad será breve y se renovará sólo tras comprobar acceso actual. Es una decisión de diseño derivada, todavía sin código o prueba navegador. [Cookies firmadas](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/private-content-signed-cookies.html).

Con ese dominio se requerirá posteriormente certificado ACM para CloudFront en us-east-1 y DNS explícito. CORS de medios permitirá sólo el origen exacto del portal, métodos y cabeceras necesarios y credenciales cuando corresponda; CSP conservará allowlist. Las políticas CORS administradas con wildcard no satisfacen ese contrato. [Certificados](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/cnames-and-https-requirements.html), [CORS configurable](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/understanding-response-headers-policies.html).

La clave privada de firma permanecerá fuera de Git/imagen/navegador; CloudFront recibirá sólo claves públicas mediante trusted key group. Rotación con solapamiento: admitir clave nueva, firmar con ella, esperar caducidad de concesiones antiguas y retirar la anterior. [Rotación oficial](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/private-content-trusted-signers.html).

Límite contractual: revocar cuenta, cancelar/suspender suscripción o retirar obra bloquea nuevas concesiones; una ya emitida puede seguir sirviendo hasta su expiración. El cliente puede haber descargado/bufferizado bytes. CloudFront comprueba el vencimiento al iniciar cada petición: una transferencia iniciada antes puede terminar después, mientras que una nueva petición Range tras vencer se rechaza. La retirada CDN inmediata y DRM requerirían otro diseño; no prometerlas. Un permiso de un archivo o prefijo no debe autorizar otras obras.

## Trabajo real pendiente

Catalog necesitará referencia de asset/version y estado de procesamiento independiente de sinopsis, con upload y publicación por Content.Manage+MFA; entrega sólo ready. Integración MediaConvert asíncrona con job ID, reintentos acotados e idempotencia, validación de salida y auditoría sin medios ni secretos. El rol MediaConvert se limita a buckets de entrada/salida. La UI necesitará reproductor HLS real y audio accesible, con selección del usuario, seek, subtítulos, cargas, error y renovación de acceso. Estas piezas todavía no existen como entrega AWS. [Rol de servicio](https://docs.aws.amazon.com/mediaconvert/latest/ug/creating-the-iam-role-in-mediaconvert-configured.html).

## QA y capacidad futura

| Nivel | Evidencia necesaria antes de habilitar AWS |
| --- | --- |
| Reglas y HTTP | No grant para anónimo, cuenta no confirmada/inactiva, suscripción cancelada/suspendida, borrador/archivado; owner sin bypass; CSRF, no-store y ausencia de PII/objetos privados en metadata. |
| AWS aislado | Bucket/distribución propios de QA y muestras sintéticas autorizadas; S3 directo rechazado, CDN sin firma/tamper/caducada rechazado incluso con caché caliente, permiso cruzado entre obras rechazado, rotación y Range reales. |
| Navegador | Escritorio/móvil con TLS normal; HLS manifiesto/segmentos reales, audio/seek, CORS/credenciales, expiración/renovación, subtítulos/teclado y errores. Emulación Chromium no acredita Safari real. |
| Volumen | Separar carga API/DB de transferencia CDN; generar desde clientes de QA con presupuesto aprobado, nunca llenar producción ni cargar el VPS compartido en paralelo. Medir inicio, buffers, errores, renovación, caché y solicitudes en Perú/móvil. |

100.000 registrados es capacidad de identidad; 1.000 reproducciones es otra carga. Ejemplo aritmético, no medición: 1.000 espectadores a 2,5 Mb/s agregan 2,5 Gb/s; a 128 kb/s de audio, 128 Mb/s. Con segmentos de seis segundos son aproximadamente 167 peticiones/s agregadas para los 1.000 espectadores con una pista y segmentos de 6 segundos (aproximadamente 0,167/s por espectador), antes de manifiestos, pistas separadas, seek y reintentos. No fijar una escalera de calidad definitiva ni acreditar capacidad sin prueba. Revisar las cuotas de la cuenta elegida. [Cuotas oficiales](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/cloudfront-limits.html).

## Requisitos para iniciar la implementación

1. Confirmar cuenta AWS de la organización, acceso temporal y acotado por canal seguro (sin claves root por chat), región, presupuesto y ambiente QA separado.
2. Entregar vídeo y podcast propios de muestra y derechos, inventario/tamaño/duración; decidir calidades y necesidad de subtítulos/descargas.
3. Aprobar dominio CDN, ventana de concesión/revocación y configuración de recursos. DNS/certificado se harán sólo con autorización posterior.
4. Validar un piloto real y sus métricas antes de ampliar volumen. No sustituir estas pruebas por mocks ni por la carga existente de 50 sesiones.
