import { useState } from 'react';
import { HashRouter, Link, Route, Routes, useParams, useSearchParams } from 'react-router-dom';
import { Brand } from '../components/Brand';
import { categories, content, type PreviewContent } from './fixtures';
function ContentCard({ item }: { item: PreviewContent }) {
  return (
    <Link className="content-card" to={'/' + item.format + '/' + item.id}>
      <div className="card-image">
        <img
          src={'/images/' + item.image + '.webp'}
          width="1672"
          height="941"
          loading="lazy"
          alt=""
        />
        <span className="card-play" aria-hidden="true">
          {item.format === 'podcast' ? '♪' : '↗'}
        </span>
        <span className="card-duration">{item.duration}</span>
      </div>
      <div className="card-meta">
        <span>{item.category}</span>
        <span>Demostración</span>
      </div>
      <h3>{item.title}</h3>
      <span className="card-link">
        {item.format === 'podcast' ? 'Ver podcast' : 'Ver contenido'}{' '}
        <span aria-hidden="true">→</span>
      </span>
    </Link>
  );
}
function Memberships() {
  const [notice, setNotice] = useState('');
  return (
    <section className="membership-section" id="memberships">
      <div className="section-heading">
        <div>
          <p className="eyebrow">UN ESPACIO PARA SEGUIR APRENDIENDO</p>
          <h2>
            Más ideas. <br />
            <em>Más posibilidades.</em>
          </h2>
        </div>
        <p>
          Referencia visual de las membresías del sitio anterior. <br />
          Precios y condiciones pendientes de validación.
        </p>
      </div>
      <div className="membership-grid">
        {[
          {
            name: 'Prueba gratuita',
            cost: '0',
            duration: '9 días',
            detail: 'Un primer encuentro con Acrópolis Channel.',
          },
          {
            name: 'Recurrencia anual',
            cost: '50',
            duration: 'al año',
            detail: 'Un camino de aprendizaje durante todo el año.',
          },
          {
            name: 'Manual anual',
            cost: '50',
            duration: 'al año',
            detail: 'Una opción de acceso con renovación manual.',
          },
        ].map((plan, index) => (
          <article className={'membership-card' + (index === 1 ? ' featured' : '')} key={plan.name}>
            <p className="eyebrow">
              {index === 1 ? 'REFERENCIA DEL SITIO ANTERIOR' : 'MEMBRESÍA DE REFERENCIA'}
            </p>
            <h3>{plan.name}</h3>
            <p className="membership-cost">
              <span>S/</span>
              {plan.cost}
              <small>{plan.duration}</small>
            </p>
            <p>{plan.detail}</p>
            <button
              className={index === 1 ? 'button' : 'button button-outline'}
              onClick={() =>
                setNotice(
                  'Esta es una referencia visual. No se ha habilitado ninguna compra ni suscripción.',
                )
              }
            >
              Ver referencia <span aria-hidden="true">→</span>
            </button>
          </article>
        ))}
      </div>
      {notice && (
        <p className="preview-action-notice" role="status">
          {notice}
        </p>
      )}
    </section>
  );
}
function PreviewHome() {
  return (
    <>
      <section className="institutional-hero preview-hero">
        <img
          className="hero-image"
          src="/images/hero-acropolis.webp"
          width="1672"
          height="941"
          fetchPriority="high"
          alt=""
        />
        <div className="hero-copy">
          <p className="eyebrow">FILOSOFÍA PARA LA VIDA</p>
          <h1>
            Conéctate con <br />
            la sabiduría <br />
            <em>del mundo.</em>
          </h1>
          <p>
            Vídeos y podcasts de charlas online. <br />
            Ideas para descubrir, reflexionar y compartir.
          </p>
          <Link className="button" to="/explore">
            Explorar contenidos<span aria-hidden="true">↗</span>
          </Link>
          <Link className="hero-secondary" to="/memberships">
            Suscripción <span aria-hidden="true">→</span>
          </Link>
          <span className="hero-caption">Nueva Acrópolis · Perú</span>
        </div>
      </section>
      <section className="category-section">
        <div className="category-intro">
          <p className="eyebrow">ENCUENTRA TU PRÓXIMA PREGUNTA</p>
          <h2>Caminos para descubrir.</h2>
        </div>
        <div className="category-grid">
          {categories.map((category, index) => (
            <Link key={category} to={'/explore?category=' + encodeURIComponent(category)}>
              <span className="category-number" aria-hidden="true">
                0{index + 1}
              </span>
              <span>{category}</span>
              <span className="category-arrow" aria-hidden="true">
                ↗
              </span>
            </Link>
          ))}
        </div>
      </section>
      <section className="editorial-section">
        <div className="section-heading">
          <div>
            <p className="eyebrow">UNA PAUSA PARA AMPLIAR LA MIRADA</p>
            <h2>
              Ideas que dejan <em>huella.</em>
            </h2>
          </div>
          <Link className="text-link" to="/explore">
            Explorar todo <span aria-hidden="true">→</span>
          </Link>
        </div>
        <div className="content-grid">
          {content.slice(0, 3).map((item) => (
            <ContentCard item={item} key={item.id} />
          ))}
        </div>
      </section>
      <section className="about-preview">
        <div className="about-image">
          <img
            src="/images/editorial-nature.webp"
            width="1672"
            height="941"
            loading="lazy"
            alt="Ilustración editorial de un paisaje andino"
          />
          <span>NUEVA ACRÓPOLIS</span>
        </div>
        <div className="about-copy">
          <p className="eyebrow">FILOSOFÍA · CULTURA · VOLUNTARIADO</p>
          <h2>
            Conocer el mundo. <br />
            <em>Conocernos mejor.</em>
          </h2>
          <p>
            Nueva Acrópolis propone una filosofía práctica, una cultura que conecta y un
            voluntariado que transforma ideas en acciones.
          </p>
          <p>
            Acrópolis Channel acerca esa búsqueda a cada pantalla. Un lugar para aprender, pensar y
            seguir creciendo en compañía.
          </p>
          <Link className="text-link" to="/institutional">
            Conoce Nueva Acrópolis <span aria-hidden="true">→</span>
          </Link>
        </div>
      </section>
      <Memberships />
      <section className="institutional-video-section">
        <p className="eyebrow">NUESTRA PROPUESTA</p>
        <h2>
          Una filosofía que <em>se vive.</em>
        </h2>
        <Link className="institutional-video" to="/institutional">
          <img
            src="/images/editorial-dialogue.webp"
            width="1672"
            height="941"
            loading="lazy"
            alt="Espacio de diálogo cultural, imagen editorial"
          />
          <span className="large-play" aria-hidden="true">
            ▶
          </span>
          <span className="video-caption">
            Conoce Nueva Acrópolis · Vista previa de vídeo institucional
          </span>
        </Link>
      </section>
      <section className="testimonials-section">
        <p className="eyebrow">EXPERIENCIAS QUE CONECTAN</p>
        <h2>
          Aprender también es <em>compartir.</em>
        </h2>
        <div className="quote-demo">
          <span aria-hidden="true">“</span>
          <blockquote>
            Un espacio para hacer una pausa, descubrir otras perspectivas y volver a la vida
            cotidiana con nuevas preguntas.
          </blockquote>
          <p>Texto ilustrativo · Testimonio pendiente de incorporar</p>
        </div>
      </section>
      <section className="faq-section">
        <div>
          <p className="eyebrow">RESOLVAMOS TUS DUDAS</p>
          <h2>
            Preguntas <br />
            <em>frecuentes.</em>
          </h2>
        </div>
        <div>
          {[
            [
              '¿Qué es Acrópolis Channel?',
              'Un espacio de Nueva Acrópolis para conectar con la filosofía y la cultura. Esta vista muestra una propuesta de diseño.',
            ],
            [
              '¿Cómo puedo explorar los contenidos?',
              'Prueba las seis categorías del sitio anterior en este prototipo. Los títulos y archivos multimedia son de demostración.',
            ],
            [
              '¿Cómo funcionan las membresías?',
              'Las opciones se muestran como referencia visual del sitio anterior. Sus condiciones se validarán antes de habilitar una suscripción.',
            ],
          ].map(([question, answer]) => (
            <details key={question}>
              <summary>{question}</summary>
              <p>{answer}</p>
            </details>
          ))}
        </div>
      </section>
    </>
  );
}
function Explore() {
  const [params, setParams] = useSearchParams();
  const category = params.get('category') ?? '';
  const [search, setSearch] = useState('');
  const items = content.filter(
    (item) =>
      (!category || item.category === category) &&
      item.title.toLocaleLowerCase('es').includes(search.toLocaleLowerCase('es')),
  );
  return (
    <div className="workspace explore-workspace">
      <div className="page-heading">
        <p className="eyebrow">EXPLORAR · CONTENIDO DE DEMOSTRACIÓN</p>
        <h1>
          Una idea puede <br />
          <em>abrir un camino.</em>
        </h1>
        <p className="lead">Encuentra una nueva forma de mirar.</p>
      </div>
      <div className="explore-controls">
        <label className="sr-only" htmlFor="content-search">
          Buscar contenido
        </label>
        <input
          id="content-search"
          type="search"
          placeholder="Busca una idea, un tema…"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
        />
        <nav aria-label="Categorías de contenido">
          <button className={!category ? 'active' : ''} onClick={() => setParams({})}>
            Todo
          </button>
          {categories.map((item) => (
            <button
              className={category === item ? 'active' : ''}
              key={item}
              onClick={() => setParams({ category: item })}
            >
              {item}
            </button>
          ))}
        </nav>
      </div>
      <p className="results-count" role="status">
        {items.length} contenidos de demostración
      </p>
      <div className="content-grid">
        {items.map((item) => (
          <ContentCard key={item.id} item={item} />
        ))}
      </div>
      {!items.length && (
        <p className="empty-state">No hay contenidos que coincidan. Prueba otra búsqueda.</p>
      )}
    </div>
  );
}
function ContentDetail({ institutional = false }: { institutional?: boolean }) {
  const { id } = useParams();
  const [tab, setTab] = useState('description');
  const item = institutional
    ? {
        id: 'institutional',
        title: 'Una filosofía que se vive',
        category: 'Nueva Acrópolis',
        format: 'video',
        duration: 'Vídeo institucional',
        image: 'editorial-dialogue',
        description:
          'Una presentación de la filosofía, la cultura y el voluntariado como caminos para aprender a vivir.',
      }
    : content.find((value) => value.id === id);
  if (!item)
    return (
      <div className="workspace">
        <h1>No encontramos este contenido.</h1>
        <Link className="text-link" to="/explore">
          Volver a explorar
        </Link>
      </div>
    );
  const podcast = item.format === 'podcast';
  return (
    <div className={'content-detail ' + (podcast ? 'podcast-detail' : '')}>
      <div className="workspace">
        <Link className="text-link back-link" to="/explore">
          ← Explorar contenidos
        </Link>
        <div className="detail-topline">
          <span>{item.category}</span>
          <span>{item.duration}</span>
          <span>Contenido de demostración</span>
        </div>
        <h1>{item.title}</h1>
        <p className="detail-lead">{item.description}</p>
        <div className={podcast ? 'podcast-player' : 'video-player'}>
          <img
            src={'/images/' + item.image + '.webp'}
            width="1672"
            height="941"
            alt=""
            loading="lazy"
          />
          <div className="media-placeholder">
            {podcast && (
              <>
                <span className="eyebrow">ACRÓPOLIS CHANNEL · PODCAST</span>
                <h2>
                  Una pausa para <br />
                  <em>escuchar.</em>
                </h2>
                <div className="audio-wave" aria-hidden="true">
                  {Array.from({ length: 30 }, (_, index) => (
                    <span key={index} />
                  ))}
                </div>
              </>
            )}
            <button
              className="preview-play"
              disabled
              aria-label={
                podcast
                  ? 'Reproducción de podcast no disponible en el prototipo'
                  : 'Reproducción de vídeo no disponible en el prototipo'
              }
            >
              ▶
            </button>
            <p>Vista previa del reproductor · Sin archivo multimedia</p>
          </div>
        </div>
        <div className="detail-bottom">
          <section className="detail-description">
            <div className="detail-tabs" role="tablist" aria-label="Información del contenido">
              <button
                role="tab"
                id="tab-description"
                aria-controls="content-description"
                aria-selected={tab === 'description'}
                tabIndex={tab === 'description' ? 0 : -1}
                onKeyDown={(event) => {
                  if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
                    setTab('transcript');
                    document.getElementById('tab-transcript')?.focus();
                  }
                }}
                onClick={() => setTab('description')}
              >
                Sobre este contenido
              </button>
              <button
                role="tab"
                id="tab-transcript"
                aria-controls="content-description"
                aria-selected={tab === 'transcript'}
                tabIndex={tab === 'transcript' ? 0 : -1}
                onKeyDown={(event) => {
                  if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
                    setTab('description');
                    document.getElementById('tab-description')?.focus();
                  }
                }}
                onClick={() => setTab('transcript')}
              >
                Transcripción
              </button>
            </div>
            <div
              role="tabpanel"
              id="content-description"
              aria-labelledby={'tab-' + (tab === 'description' ? 'description' : 'transcript')}
            >
              <p>
                {tab === 'description'
                  ? item.description
                  : 'Espacio de ejemplo para una transcripción accesible. No se ha incorporado un archivo ni una transcripción real.'}
              </p>
              <p className="field-help">
                Esta ficha es un prototipo de diseño. Los metadatos y textos son ilustrativos.
              </p>
            </div>
          </section>
          <aside className="detail-aside">
            <p className="eyebrow">SIGUE DESCUBRIENDO</p>
            <h3>
              Siempre hay <br />
              otra pregunta.
            </h3>
            <Link className="text-link" to="/explore">
              Explorar contenidos →
            </Link>
          </aside>
        </div>
        {!institutional && (
          <section className="related-content">
            <div className="section-heading">
              <h2>
                Para seguir <em>pensando.</em>
              </h2>
            </div>
            <div className="content-grid">
              {content
                .filter((value) => value.id !== item.id)
                .slice(0, 3)
                .map((value) => (
                  <ContentCard item={value} key={value.id} />
                ))}
            </div>
          </section>
        )}
      </div>
    </div>
  );
}
export function PreviewRoutes() {
  return (
    <div className="app-shell">
      <a
        className="skip-link"
        href="#preview-main"
        onClick={(event) => {
          event.preventDefault();
          document.getElementById('preview-main')?.focus();
        }}
      >
        Saltar al contenido
      </a>
      <div className="preview-banner">
        Vista previa de diseño · Contenido de demostración · Sin compras ni suscripciones activas
      </div>
      <header className="site-header">
        <div className="header-inner">
          <Brand preview />
          <nav aria-label="Navegación principal">
            <Link to="/explore">Explorar</Link>
            <Link to="/memberships">Suscripción</Link>
            <button
              className="button button-small prototype-disabled"
              disabled
              aria-label="Ingresar: disponible en la aplicación principal"
            >
              Ingresar ↗
            </button>
          </nav>
        </div>
      </header>
      <main id="preview-main" tabIndex={-1}>
        <Routes>
          <Route path="/" element={<PreviewHome />} />
          <Route path="/explore" element={<Explore />} />
          <Route path="/video/:id" element={<ContentDetail />} />
          <Route path="/podcast/:id" element={<ContentDetail />} />
          <Route path="/institutional" element={<ContentDetail institutional />} />
          <Route
            path="/memberships"
            element={
              <>
                <h1 className="sr-only">Membresías de referencia</h1>
                <Memberships />
              </>
            }
          />
          <Route
            path="*"
            element={
              <div className="workspace">
                <h1>Esta página no está aquí.</h1>
                <Link to="/">Volver al inicio</Link>
              </div>
            }
          />
        </Routes>
      </main>
      <footer className="site-footer">
        <div className="footer-inner">
          <Brand />
          <p>
            Filosofía, cultura y voluntariado. <br />
            Nueva Acrópolis · Perú
          </p>
          <span>Vista previa de diseño</span>
        </div>
      </footer>
    </div>
  );
}
export default function Preview() {
  return (
    <HashRouter>
      <PreviewRoutes />
    </HashRouter>
  );
}
