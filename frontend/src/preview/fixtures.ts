export const categories = [
  'Lecturas',
  'Documentales',
  'Videos',
  'Podcast',
  'Charlas online',
  'Cursos',
] as const;
export type PreviewContent = {
  id: string;
  title: string;
  category: (typeof categories)[number];
  format: 'video' | 'podcast';
  duration: string;
  image: string;
  description: string;
};
export const content: PreviewContent[] = [
  {
    id: 'filosofia-cotidiana',
    title: 'La filosofía empieza en lo cotidiano',
    category: 'Videos',
    format: 'video',
    duration: '24 min',
    image: 'editorial-dialogue',
    description:
      'Una invitación a observar nuestras decisiones y encontrar nuevas preguntas en la vida diaria.',
  },
  {
    id: 'arte-de-escuchar',
    title: 'El arte de escuchar',
    category: 'Podcast',
    format: 'podcast',
    duration: '18 min',
    image: 'editorial-podcast',
    description:
      'Una pausa para pensar en el diálogo, la atención y la manera en que nos encontramos con los demás.',
  },
  {
    id: 'leer-el-mundo',
    title: 'Leer para mirar el mundo de otra manera',
    category: 'Lecturas',
    format: 'video',
    duration: '12 min',
    image: 'editorial-reading',
    description:
      'La lectura como puerta de entrada a otras experiencias y otras formas de entender.',
  },
  {
    id: 'naturaleza-maestra',
    title: 'La naturaleza también nos enseña',
    category: 'Documentales',
    format: 'video',
    duration: '32 min',
    image: 'editorial-nature',
    description:
      'Una mirada a los ciclos naturales como inspiración para comprender nuestro propio camino.',
  },
  {
    id: 'preguntas-esenciales',
    title: 'Las preguntas que nos acompañan',
    category: 'Charlas online',
    format: 'video',
    duration: '45 min',
    image: 'editorial-dialogue',
    description: 'Un espacio de conversación sobre lo que da sentido a nuestra vida.',
  },
  {
    id: 'aprender-a-vivir',
    title: 'Aprender a vivir con más conciencia',
    category: 'Cursos',
    format: 'video',
    duration: '6 encuentros',
    image: 'editorial-reading',
    description:
      'Un recorrido visual de ejemplo para presentar futuras experiencias de aprendizaje.',
  },
];
