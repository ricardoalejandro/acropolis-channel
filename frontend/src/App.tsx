import { Greeting } from './features/greeting/Greeting';

export default function App() {
  return (
    <div className="site-shell">
      <header className="site-header">
        <span className="brand-mark" aria-hidden="true">
          AC
        </span>
        <span className="brand-name">Nueva Acrópolis</span>
      </header>
      <main className="hero">
        <section className="introduction" aria-labelledby="page-title">
          <p className="eyebrow">Educación y cultura</p>
          <h1 id="page-title">
            Acrópolis
            <br />
            Channel
            <span className="title-dot" aria-hidden="true">
              .
            </span>
          </h1>
          <p className="intro-copy">Un espacio para descubrir, aprender y crecer.</p>
        </section>
        <Greeting />
      </main>
      <footer className="site-footer">Nueva Acrópolis · Acrópolis Channel</footer>
    </div>
  );
}
