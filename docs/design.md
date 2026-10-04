# Diseño y alcance visual

La referencia de estructura es Acrópolis Channel Perú y las ocho capturas aportadas por el propietario el 4 de octubre de 2026. BBVA Aprendemos Juntos y DOCUMENTARY+ aportan inspiración visual; no definen categorías ni reglas del producto. El navegador no pudo verificar la política administrativa del sitio de referencia, por lo que las capturas son la evidencia visual comprobada.

## Dirección

Una plataforma cultural contemporánea, sobria y atractiva para jóvenes adultos: tipografía editorial, imágenes protagonistas, lectura cómoda y navegación ágil. Preservar el reconocimiento de Acrópolis, su relación con Nueva Acrópolis Perú y el mensaje «Conéctate con la sabiduría del mundo».

La interfaz usa azul #126CA5, azul profundo #16374B, marfil #F7F5F0, texto #243B4A y naranja #BE510F para acciones puntuales. Blanco sobre el azul propuesto ofrece contraste 5,65:1 y blanco sobre el naranja 4,80:1; estos cálculos no sustituyen verificar cada combinación real. El verde queda en el distintivo Perú de la marca. Inter sirve navegación, formularios y textos; Source Serif 4 aporta carácter editorial. Ambas fuentes se sirven desde la aplicación.

La marca implementada reproduce tipográficamente las palabras, ondas y distintivo visibles en las capturas. No se ha obtenido el archivo vectorial oficial: debe sustituirse por ese archivo cuando esté disponible, conservando proporción, espacio y nombre accesible.

## Estructura confirmada

Las seis entradas del catálogo son Lecturas, Documentales, Videos, Podcast, Charlas online y Cursos. La portada incluye presentación, categorías, información de Nueva Acrópolis, vídeo institucional, membresías, testimonios y preguntas frecuentes.

Las fichas de vídeo y podcast son propuestas de interfaz con datos de demostración. Sus campos internos todavía no se han verificado contra el sistema antiguo. Capítulos, transcripciones, nuevas taxonomías o reglas de acceso requieren definición en la entrega de catálogo.

El prototipo se construye por separado en dist-preview. Su navegación es interactiva, pero los contenidos, reproductores y contratación están identificados como demostración. No debe incluirse preview.html ni sus fixtures en el bundle productivo. La aplicación productiva de esta fase ofrece cuentas, perfil y administración.

Las membresías de las capturas (prueba de nueve días y planes anuales de S/50) son referencias del sistema anterior. Su presencia en la maqueta no configura cobros, derechos de acceso ni reglas comerciales.

## Imágenes y procedencia

Las cinco imágenes son ilustraciones editoriales originales generadas con la herramienta integrada image_gen, no fotografías documentales ni imágenes de actos o miembros reales. Los PNG de origen se conservan de forma privada; el sitio utiliza WebP. No se debe identificar el busto como una persona histórica concreta ni atribuir los escenarios a actividades de Nueva Acrópolis.

| Archivo en frontend/public/images | Uso |
| --- | --- |
| hero-acropolis.webp | Presentación principal |
| editorial-reading.webp | Lecturas |
| editorial-podcast.webp | Audio |
| editorial-dialogue.webp | Diálogo y aprendizaje |
| editorial-nature.webp | Cultura y paisaje andino |

Tamaño conjunto inicial de los cinco WebP: aproximadamente 532 KiB. La imagen principal pesa aproximadamente 89 KiB. Evitar cargar anticipadamente imágenes de secciones inferiores.

### Prompt de la imagen principal

Use case: ads-marketing. Asset type: wide cinematic editorial hero photograph for Acropolis Channel, an educational philosophy and culture platform for thoughtful young adults in Peru. Create an original exceptionally refined visual: close three-quarter profile of a weathered ancient Greek marble philosopher bust in the right half of the frame, evocative distant classical stone colonnade in a Mediterranean landscape, atmospheric natural late-afternoon side light. The scene must feel like a contemporary high-end cultural magazine photograph, timeless but fresh, tactile stone, very subtle photographic grain, sophisticated deep blue teal shadows and restrained honey-warm highlights, authentic believable material and depth. Composition landscape 16:9, main sculptural head on right, generous dark quietly textured negative space on left for live website typography (do NOT draw any typography), dramatic yet calm, strong beautiful silhouette and fine details, gentle natural background bokeh, avoid a kitschy fantasy montage. No logos, letters, watermarks, UI, frames, book covers or captions. No neon, no excessive orange overlay, no glowing eyes, no modern gadgets. Keep left background dark navy with nuanced texture and continuous composition. Full-bleed photographic art direction.

### Prompts de las imágenes de apoyo

Contexto compartido: imagen editorial horizontal 16:9 para Acrópolis Channel, filosofía y cultura para jóvenes adultos; tratamiento sobrio contemporáneo, materiales realistas, grano sutil, sin texto, logotipos, interfaz ni marcas de agua.

- **lecturas:** A beautifully worn open philosophy book on a quiet light stone desk, side-lit by a tall window, a single olive twig next to it, deep ink-blue shadow and soft warm cream pages. Close crop, no readable words. Beautiful natural papery texture and restrained contemporary editorial composition.
- **podcast:** A refined dark studio photograph of a single high-quality vintage-style microphone, foreground sharply focused, soft teal-blue and amber light, thoughtful intimate atmosphere. No people, no brand name or lettering, no colored neon. Broad cinematic negative space.
- **dialogo:** A candid editorial close-up of the hands and notebooks of a small group of diverse young adults discussing ideas around a wooden table in a sunlit library. Faces not visible. Warm human natural light, blue and cream palette, authentic tactile notebooks, no readable writing, no staged corporate gestures. Camera at table height, intimate thoughtful atmosphere.
- **documentales:** Dramatic but serene editorial photograph of ancient Andean stone terraces and cloud-wrapped green mountains of Peru at first morning light. Authentic stone and mist, sophisticated desaturated greens and restrained warm sunrise light, no humans, no text. Contemporary cultural documentary photography.

## Revisión visual

Verificar 1440, 1024, 390 y 360 píxeles, zoom, navegación por teclado, foco visible, movimiento reducido, títulos largos, contenido ausente, carga y errores. Los controles deben permanecer legibles sobre imágenes y el tamaño de los botones debe permitir uso táctil cómodo. Las capturas y los resultados automatizados se conservan en el informe privado de QA de cada ejecución.

Referencias: https://aprendemosjuntos.bbva.com/especial/el-cerebro-nuestro-mejor-aliado-contra-el-estres-marian-rojas-estape/ y https://docplus.com/home.
