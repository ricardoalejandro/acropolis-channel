import { useCallback, useState, type ReactNode } from 'react';
import { Link, useParams, useSearchParams, type SetURLSearchParams } from 'react-router-dom';
import {
  catalog,
  categories,
  categoryLabel,
  durationLabel,
  contentQuery,
  type ContentSummary,
} from '../../api/catalog';
import { useCatalog } from './useCatalog';
import { Icon } from '../../components/Icon';
import { CollectionItems, WorkAccess, SequenceNavigation } from './Work';
import './catalog.css';
import './topics.css';
import { PublicTopicFilter } from './PublicTopicFilter';
export function ContentCard({ item }: { item: ContentSummary }) {
  return (
    <Link
      className={'catalog-card catalog-card-' + item.category}
      to={'/content/' + encodeURIComponent(item.slug)}
    >
      <div className={'catalog-card-image' + (item.coverAsset ? '' : ' catalog-no-image')}>
        {item.coverAsset ? (
          <img
            src={'/images/' + item.coverAsset + '.webp'}
            width="1672"
            height="941"
            loading="lazy"
            alt=""
          />
        ) : (
          <Icon
            name={
              item.category === 'lecturas'
                ? 'book'
                : item.category === 'podcast'
                  ? 'audio'
                  : item.category === 'cursos'
                    ? 'course'
                    : 'video'
            }
          />
        )}
        {item.durationSeconds !== null && (
          <span className="catalog-duration">{durationLabel(item.durationSeconds)}</span>
        )}
      </div>
      <div className="catalog-card-meta">
        <span>{categoryLabel(item.category)}</span>
        {item.isFree && <span className="catalog-free">Gratuito</span>}
      </div>
      <h3>{item.title}</h3>
      {item.author && <p className="catalog-author">{item.author}</p>}
      {item.summary && <p>{item.summary}</p>}
      <span className="catalog-card-link">Ver ficha</span>
    </Link>
  );
}
export function CategoryStrip() {
  return (
    <section className="catalog-categories">
      <div className="catalog-section-heading">
        <p className="eyebrow">Elige qué explorar</p>
        <h2>Categorías</h2>
      </div>
      <div className="catalog-category-grid">
        {categories.map((category) => (
          <Link key={category.id} to={'/explore?category=' + category.id}>
            <Icon
              name={
                category.id === 'lecturas'
                  ? 'book'
                  : category.id === 'podcast'
                    ? 'audio'
                    : category.id === 'documentales'
                      ? 'film'
                      : category.id === 'cursos'
                        ? 'course'
                        : category.id === 'charlas-online'
                          ? 'talk'
                          : 'video'
              }
            />
            <span>{category.label}</span>
          </Link>
        ))}
      </div>
    </section>
  );
}
export function CatalogState({
  loading,
  error,
  retry,
  children,
}: {
  loading: boolean;
  error: string;
  retry: () => void;
  children: ReactNode;
}) {
  if (loading)
    return (
      <p className="catalog-state" role="status">
        Cargando contenidos…
      </p>
    );
  if (error)
    return (
      <div className="catalog-state">
        <p role="alert">{error}</p>
        <button className="button button-outline button-small" onClick={retry}>
          Reintentar
        </button>
      </div>
    );
  return children;
}
export function LatestContent() {
  const load = useCallback(() => catalog.list({ pageSize: 3 }), []);
  const state = useCatalog(load, 'home');
  return (
    <section className="catalog-editorial">
      <div className="catalog-section-heading">
        <div>
          <p className="eyebrow">Nuevas publicaciones</p>
          <h2>Novedades de la mediateca</h2>
        </div>
        <Link className="text-link" to="/explore">
          Explorar todo
        </Link>
      </div>
      <CatalogState loading={state.loading} error={state.error} retry={state.reload}>
        {state.data?.items.length ? (
          <div className="catalog-grid">
            {state.data.items.map((item) => (
              <ContentCard item={item} key={item.id} />
            ))}
          </div>
        ) : (
          <div className="catalog-empty">
            <h3>El catálogo está por comenzar.</h3>
            <p>
              Aún no hay contenidos publicados. Puedes recorrer las categorías y preparar tu acceso
              gratuito.
            </p>
          </div>
        )}
      </CatalogState>
    </section>
  );
}
export function CatalogFilters({
  search,
  setSearch,
  category,
  setCategory,
  onSubmit,
  children,
}: {
  search: string;
  setSearch: (value: string) => void;
  category: string;
  setCategory: (value: string) => void;
  onSubmit: () => void;
  children?: ReactNode;
}) {
  return (
    <form
      className="catalog-filters"
      onSubmit={(event) => {
        event.preventDefault();
        onSubmit();
      }}
    >
      <div className="field">
        <label htmlFor="content-search">Buscar contenido</label>
        <input
          id="content-search"
          type="search"
          maxLength={100}
          placeholder="Una idea, un tema…"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
        />
      </div>
      <div className="field">
        <label htmlFor="content-category">Categoría</label>
        <select
          id="content-category"
          value={category}
          onChange={(event) => setCategory(event.target.value)}
        >
          <option value="">Todas las categorías</option>
          {categories.map((item) => (
            <option key={item.id} value={item.id}>
              {item.label}
            </option>
          ))}
        </select>
      </div>
      {children}
      <button className="button button-small" type="submit">
        Buscar
      </button>
    </form>
  );
}
export function Pagination({
  page,
  pageSize,
  total,
  change,
}: {
  page: number;
  pageSize: number;
  total: number;
  change: (page: number) => void;
}) {
  return (
    <nav className="pagination" aria-label="Paginación de contenidos">
      <button
        className="button button-outline button-small"
        disabled={page <= 1}
        onClick={() => change(page - 1)}
      >
        Anterior
      </button>
      <span>Página {page}</span>
      <button
        className="button button-outline button-small"
        disabled={page * pageSize >= total}
        onClick={() => change(page + 1)}
      >
        Siguiente
      </button>
    </nav>
  );
}
export function Explore() {
  const [params, setParams] = useSearchParams();
  return <ExploreView key={params.toString()} params={params} setParams={setParams} />;
}
function ExploreView({
  params,
  setParams,
}: {
  params: URLSearchParams;
  setParams: SetURLSearchParams;
}) {
  const query = contentQuery(params);
  const [search, setSearch] = useState(query.search ?? '');
  const [category, setCategory] = useState(query.category ?? '');
  const [topic, setTopic] = useState(query.topic ?? '');
  const key = params.toString();
  const load = useCallback(() => catalog.list(contentQuery(new URLSearchParams(key))), [key]);
  const state = useCatalog(load, key);
  function change(page = 1, applied = false) {
    const next = new URLSearchParams();
    const selectedSearch = applied ? (query.search ?? '') : search.trim();
    const selectedCategory = applied ? (query.category ?? '') : category;
    if (selectedSearch) next.set('search', selectedSearch);
    if (selectedCategory) next.set('category', selectedCategory);
    const selectedTopic = applied ? (query.topic ?? '') : topic;
    if (selectedTopic) next.set('topic', selectedTopic);
    if (page > 1) next.set('page', String(page));
    setParams(next);
  }
  return (
    <div className="workspace catalog-explore">
      <div className="page-heading">
        <p className="eyebrow">Mediateca</p>
        <h1>Explorar la mediateca</h1>
        <p className="lead">Encuentra lecturas, vídeos, podcasts y cursos.</p>
      </div>
      <CatalogFilters
        search={search}
        setSearch={setSearch}
        category={category}
        setCategory={setCategory}
        onSubmit={() => change()}
      >
        <PublicTopicFilter value={topic} change={setTopic} />
      </CatalogFilters>
      <CatalogState loading={state.loading} error={state.error} retry={state.reload}>
        {state.data && (
          <>
            <p className="results-count" role="status">
              {state.data.total} {state.data.total === 1 ? 'contenido' : 'contenidos'}
            </p>
            {state.data.items.length ? (
              <div className="catalog-grid">
                <h2 className="sr-only">Resultados de la búsqueda</h2>
                {state.data.items.map((item) => (
                  <ContentCard item={item} key={item.id} />
                ))}
              </div>
            ) : (
              <div className="catalog-empty">
                <h2>
                  {query.search || query.category || query.topic
                    ? 'No encontramos coincidencias.'
                    : 'El catálogo está por comenzar.'}
                </h2>
                <p>
                  {query.search || query.category
                    ? 'Prueba otra búsqueda o una categoría diferente.'
                    : 'Aún no hay contenidos publicados. Vuelve pronto para descubrir nuevas ideas.'}
                </p>
              </div>
            )}
            <Pagination
              page={state.data.page}
              pageSize={state.data.pageSize}
              total={state.data.total}
              change={(page) => change(page, true)}
            />
          </>
        )}
      </CatalogState>
    </div>
  );
}
export function ContentPage() {
  const { slug = '' } = useParams();
  const load = useCallback(() => catalog.detail(slug), [slug]);
  const state = useCatalog(load, slug);
  const item = state.data;
  return (
    <div className="workspace catalog-detail">
      <Link className="text-link back-link" to="/explore">
        ← Explorar contenidos
      </Link>
      {item && (
        <div className="catalog-topline">
          <span>{categoryLabel(item.category)}</span>
          <span>{item.isFree ? 'Acceso gratuito' : 'Requiere plan'}</span>
          {item.durationSeconds !== null && <span>{durationLabel(item.durationSeconds)}</span>}
        </div>
      )}
      <h1>{item?.title ?? 'Contenido'}</h1>
      <CatalogState loading={state.loading} error={state.error} retry={state.reload}>
        {item && (
          <>
            {item.author && <p className="catalog-author">{item.author}</p>}
            <p className="catalog-lead">{item.summary}</p>
            {Boolean(item.tags?.length) && (
              <div className="tag-list">
                {item.tags?.map((tag) => (
                  <span className="tag" key={tag}>
                    {tag}
                  </span>
                ))}
              </div>
            )}
            {item.coverAsset && (
              <img
                className="catalog-detail-cover"
                src={'/images/' + item.coverAsset + '.webp'}
                width="1672"
                height="941"
                alt=""
              />
            )}
            <section className="catalog-synopsis">
              <h2>Sobre este contenido</h2>
              <div className="catalog-body">{item.body}</div>
            </section>
            <CollectionItems item={item} />
            <WorkAccess item={item} />
            <SequenceNavigation slug={item.slug} />
          </>
        )}
      </CatalogState>
    </div>
  );
}
