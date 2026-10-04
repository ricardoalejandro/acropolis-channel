import { Link } from 'react-router-dom';
import { useSession } from '../../auth/useSession';
import { CategoryStrip, LatestContent } from '../catalog/Catalog';
export function Home() {
  const { user } = useSession();
  return (
    <>
      <section className="institutional-hero">
        <img
          className="hero-image"
          src="/images/hero-acropolis.webp"
          width="1672"
          height="941"
          fetchPriority="high"
          alt=""
        />
        <div className="hero-copy">
          <p className="eyebrow">FILOSOFÍA · CULTURA · VOLUNTARIADO</p>
          <h1>
            Conéctate con <br />
            la sabiduría <br />
            <em>del mundo.</em>
          </h1>
          <p>
            Hay ideas que cambian nuestra manera de mirar.
            <br className="desktop-break" /> Y preguntas que nos ayudan a vivir mejor.
          </p>
          <div className="hero-actions">
            <Link className="button" to="/explore">
              Explorar contenidos <span aria-hidden="true">↗</span>
            </Link>
            <Link className="hero-account" to={user ? '/profile' : '/register'}>
              {user ? 'Ir a mi perfil' : 'Crear mi cuenta'}
              <span aria-hidden="true">↗</span>
            </Link>
          </div>
          <span className="hero-caption">Acrópolis Channel · Nueva Acrópolis Perú</span>
        </div>
        <span className="hero-art-coordinate">FILOSOFÍA PARA LA VIDA</span>
      </section>
      <CategoryStrip />
      <LatestContent />
      <section className="intro-section">
        <div>
          <p className="eyebrow">EL VALOR DE HACERSE PREGUNTAS</p>
          <h2>
            La cultura nos acerca. <br />
            <em>La filosofía nos transforma.</em>
          </h2>
        </div>
        <div>
          <p>
            Nueva Acrópolis propone una filosofía práctica: conocernos mejor, aprender del mundo y
            participar en él con conciencia.
          </p>
          <p>
            Acrópolis Channel es un punto de encuentro con esa búsqueda. Tu cuenta es el primer paso
            para formar parte de este espacio.
          </p>
          <Link className="text-link" to={user ? '/profile' : '/login'}>
            {user ? 'Ver mi cuenta' : 'Ya tengo una cuenta'} <span aria-hidden="true">→</span>
          </Link>
        </div>
      </section>
      <section className="values-strip" aria-label="Nuestro propósito">
        <div>
          <span>01</span>
          <h3>Descubrir</h3>
          <p>Ampliar la mirada con nuevas ideas.</p>
        </div>
        <div>
          <span>02</span>
          <h3>Reflexionar</h3>
          <p>Encontrar sentido en lo cotidiano.</p>
        </div>
        <div>
          <span>03</span>
          <h3>Compartir</h3>
          <p>Crecer en compañía y actuar mejor.</p>
        </div>
      </section>
    </>
  );
}
