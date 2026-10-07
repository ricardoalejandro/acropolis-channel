import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useSession } from '../../auth/useSession';
import { CategoryStrip, LatestContent } from '../catalog/Catalog';
import { Icon } from '../../components/Icon';
export function Home() {
  const { user } = useSession();
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  return (
    <>
      <section className="library-intro">
        <div className="library-intro-copy">
          {user && <p className="welcome">Hola, {user.displayName.split(' ')[0]}.</p>}
          <h1>Tu mediateca cultural.</h1>
          <p className="lead">Lecturas, documentales y voces para aprender a vivir.</p>
          <form
            className="library-search"
            onSubmit={(event) => {
              event.preventDefault();
              navigate(
                '/explore' + (search.trim() ? '?search=' + encodeURIComponent(search.trim()) : ''),
              );
            }}
          >
            <label className="sr-only" htmlFor="home-search">
              Buscar en la mediateca
            </label>
            <Icon name="search" />
            <input
              id="home-search"
              type="search"
              maxLength={100}
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="Busca una idea, una obra…"
            />
            <button className="button" type="submit">
              Buscar
            </button>
          </form>
          <p className="library-access">
            Durante esta etapa, el acceso es gratuito.{' '}
            <Link to={user ? '/profile/subscription' : '/register'}>
              {user ? 'Ver mi suscripción' : 'Crear mi cuenta'}
            </Link>
          </p>
        </div>
        <div className="library-purpose">
          <span className="library-mark">Nueva Acrópolis</span>
          <p>Un lugar para descubrir, comprender y compartir cultura.</p>
          <Link to="/explore">Explorar contenidos</Link>
        </div>
      </section>
      <CategoryStrip />
      <LatestContent />
      <section className="institution-note">
        <h2>Filosofía para la vida cotidiana.</h2>
        <p>
          Acrópolis Channel acerca la filosofía, la cultura y el voluntariado de Nueva Acrópolis
          Perú a tu día a día.
        </p>
      </section>
    </>
  );
}
