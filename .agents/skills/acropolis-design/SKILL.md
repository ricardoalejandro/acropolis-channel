---
name: acropolis-design
description: Diseñar, rediseñar y revisar la interfaz pública y el backoffice de Acrópolis Channel desde WSL con identidad propia, accesibilidad y validación visual aislada. Usar para cualquier cambio de frontend, tipografía, paleta, navegación, formularios o reproducción.
---

# Diseño de Acrópolis Channel

Leer `AGENTS.md`, `docs/wsl-development.md` y `docs/design.md` del checkout de trabajo. Desarrollar en `/home/rrojacam/projects/acropolis-channel` de WSL Ubuntu 24.04; desde Codex Windows ejecutar Linux con `wsl.exe -d Ubuntu-24.04 --cd /home/rrojacam/projects/acropolis-channel -- bash -lc 'COMANDO'`. Mantener reglas y código versionados, sincronizados por Git. La certificación de publicación y producción permanecen en `/root/proyect/acropolis-channel` del VPS, accesible con `ssh -o BatchMode=yes -o StrictHostKeyChecking=yes vps`. `.local/` conserva evidencias del entorno en uso; no copiar materiales productivos a WSL. El encargo actual del propietario prevalece sobre una captura o regla archivada.

## Antes de editar

Determinar la tarea concreta del visitante y revisar la pantalla y el comportamiento existentes. Para nuevas superficies, fijar jerarquía, recorrido y datos reales antes de elegir componentes. Leer references/review.md. No ejecutar instaladores, binarios o hooks de referencias externas: esta skill es una adaptación específica, no el launcher Impeccable.

## Construir

- La mediateca presenta obras reales con títulos, autor, formato y contexto. Una lectura, una serie y un vídeo necesitan composiciones distintas. Si el catálogo está vacío, comunicarlo honestamente y ofrecer navegación útil.
- Source Sans 3 para controles y Literata para títulos culturales/lectura; fuentes locales, licencias y pesos acotados. Usar los tokens y reglas de docs/design.md.
- Backoffice independiente: navegación persistente, filtros, tablas, formularios y acciones visibles según permisos. La autoridad se verifica en servidor; ocultar un botón no basta.
- Formularios con labels, estados pendiente/error/éxito, foco útil, bloqueo sólo durante petición y prevención de pérdida de cambios. Las acciones sensibles requieren intención clara.
- Reproducción sólo por elección del usuario. YouTube usa su reproductor oficial con controles y errores reales; no extraer audio ni presentar vídeos públicos como exclusivos. La lectura completa se representa como texto seguro.
- No añadir precios, cifras, testimonios, fotografías institucionales o funcionalidades ficticias. No reutilizar el busto como hero de todas las pantallas, ni decoraciones numeradas, etiquetas mayúsculas y flechas en cada bloque.

## Verificar y entregar

Ejecutar componentes/contratos y recorridos del gate acropolis-quality. Inspeccionar escritorio y móvil en QA aislado: composición, contenido largo, teclado, foco, contraste, zoom, vacíos, errores, reducción de movimiento y ausencia de desbordamiento. Corregir defectos comprobados en un lote y confirmar; no sustituir la validación por pulido indefinido.

Guardar capturas e informe privados. Distinguir revisión QA de navegador público y respetar sus restricciones. Actualizar docs/design.md cuando cambien decisiones duraderas, manteniendo su correspondencia con tokens y componentes.

Referencias y procedencia: references/sources.md. No sincronizar automáticamente referencias de ramas main ni aplicar recetas de otra marca.
