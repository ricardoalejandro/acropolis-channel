import { useCallback, useRef } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useSession } from '../../auth/useSession';
import { ApiError } from '../../api/identity';
import { catalog, type ContentDetail } from '../../api/catalog';
import { consumption, type ContentWork } from '../../api/consumption';
import { subscriptionMessage } from '../../api/subscriptions';
import { useConsumptionRecording } from './useConsumptionRecording';
import { useCatalog } from './useCatalog';
import { CatalogState } from './Catalog';
import { YouTubePlayer } from './YouTubePlayer';
export function CollectionItems({ item }: { item: ContentDetail }) {
  if (!item.collectionKind || !item.items?.length) return null;
  return (
    <section className="collection-items">
      <h2>
        {item.collectionKind === 'program' ? 'Cursos de este programa' : 'Obras de este curso'}
      </h2>
      <ol>
        {item.items.map((child) => (
          <li key={child.id}>
            <Link
              aria-label={child.author ? child.title + ' · ' + child.author : child.title}
              aria-describedby={child.isFree ? 'collection-free-' + child.id : undefined}
              to={
                '/content/' +
                encodeURIComponent(child.slug) +
                '?collection=' +
                encodeURIComponent(item.slug)
              }
            >
              <span>{child.title}</span>
              {child.author && <small>{child.author}</small>}
              {child.isFree && <small id={'collection-free-' + child.id}>Gratuito</small>}
            </Link>
          </li>
        ))}
      </ol>
    </section>
  );
}
export function WorkAccess({ item }: { item: ContentDetail }) {
  const { user, loading } = useSession();
  return loading ? (
    <p role="status">Comprobando tu acceso…</p>
  ) : !user ? (
    <section className="work-gate">
      <h2>Accede a la obra completa.</h2>
      <p>
        {item.isFree
          ? 'Crea una cuenta, confirma tu correo y activa el plan Gratuito para acceder a esta obra.'
          : 'Crea una cuenta y consulta los planes Probacionismo y Anual para acceder a esta obra.'}
      </p>
      <div className="subscription-actions">
        <Link className="button" to="/login" state={{ returnTo: '/content/' + item.slug }}>
          Ingresar
        </Link>
        <Link className="button button-outline" to="/register">
          Crear mi cuenta
        </Link>
      </div>
    </section>
  ) : (
    <AuthorizedWork key={item.slug + user.id} item={item} />
  );
}
function AuthorizedWork({ item }: { item: ContentDetail }) {
  const load = useCallback(() => consumption.content(item.slug), [item.slug]);
  const state = useCatalog(load, item.slug);
  if (state.failure instanceof ApiError && state.failure.status === 403)
    return (
      <section className="work-gate">
        <h2>
          {state.failure.code === 'content_requires_plan'
            ? 'Esta obra requiere otro plan.'
            : state.failure.code === 'subscription_suspended'
              ? 'Tu acceso está suspendido.'
              : 'Revisa tu suscripción.'}
        </h2>
        <p>
          {subscriptionMessage(state.failure)}
        </p>
        <Link
          className="button"
          to={'/profile/subscription?content=' + encodeURIComponent(item.slug)}
        >
          Ver mi suscripción
        </Link>
      </section>
    );
  if (state.failure instanceof ApiError && state.failure.status === 404)
    return (
      <p className="catalog-state">
        Esta ficha aún no tiene una obra disponible. Puedes seguir explorando la mediateca.
      </p>
    );
  return (
    <CatalogState loading={state.loading} error={state.error} retry={state.reload}>
      {state.data && (
        <>
          {state.data.workText !== null && (
            <RecordedReading key={state.data.id + state.data.version} work={state.data} />
          )}
          {state.data.youTubeId !== null && (
            <YouTubePlayer
              key={state.data.id + ':' + state.data.version + ':' + state.data.youTubeId}
              videoId={state.data.youTubeId}
              title={item.title}
              recording={{ slug: state.data.slug, version: state.data.version }}
              podcast={item.category === 'podcast'}
            />
          )}
          {state.data.collectionKind && (
            <p className="collection-access" role="status">
              Tu acceso a {state.data.collectionKind === 'program' ? 'este programa' : 'este curso'}{' '}
              está activo. Elige un contenido de la lista.
            </p>
          )}
        </>
      )}
    </CatalogState>
  );
}
function RecordedReading({ work }: { work: ContentWork }) {
  const article = useRef<HTMLElement>(null);
  const read = useCallback(() => {
    if (!article.current) return null;
    const bounds = article.current.getBoundingClientRect();
    return {
      now: performance.now(),
      visible: document.visibilityState === 'visible',
      focused: document.hasFocus(),
      articleTop: bounds.top,
      articleHeight: bounds.height,
      viewportHeight: window.innerHeight,
    };
  }, []);
  useConsumptionRecording({ slug: work.slug, version: work.version }, 'reading', read);
  return (
    <article ref={article} className="reading-work" aria-label="Lectura completa">
      <h2>Lectura completa</h2>
      {work
        .workText!.split(/\n\s*\n/)
        .filter(Boolean)
        .map((paragraph, index) => (
          <p key={index}>{paragraph}</p>
        ))}
    </article>
  );
}
export function SequenceNavigation({ slug }: { slug: string }) {
  const [params] = useSearchParams();
  const parent = params.get('collection') ?? '';
  return /^[a-z0-9]+(-[a-z0-9]+)*$/.test(parent) && parent !== slug ? (
    <Sequence key={parent + slug} parent={parent} slug={slug} />
  ) : null;
}
function Sequence({ parent, slug }: { parent: string; slug: string }) {
  const load = useCallback(() => catalog.detail(parent), [parent]);
  const state = useCatalog(load, parent);
  const items = state.data?.items ?? [];
  const index = items.findIndex((item) => item.slug === slug);
  if (index < 0) return null;
  const previous = items[index - 1];
  const next = items[index + 1];
  return (
    <nav className="sequence-navigation" aria-label="Navegación del curso o programa">
      <Link to={'/content/' + parent}>Volver a {state.data?.title}</Link>
      <div>
        {previous && (
          <Link
            className="button button-outline"
            to={'/content/' + previous.slug + '?collection=' + parent}
          >
            Anterior: {previous.title}
          </Link>
        )}
        {next && (
          <Link className="button" to={'/content/' + next.slug + '?collection=' + parent}>
            Siguiente: {next.title}
          </Link>
        )}
      </div>
    </nav>
  );
}
