import { Link } from 'react-router-dom';
export function Brand({ preview = false }: { preview?: boolean }) {
  return (
    <Link className="brand" to="/" aria-label="Acrópolis Channel, inicio">
      <span className="brand-wordmark">
        <strong>acrópolis</strong>
        <span>
          channel<span className="country">Perú</span>
        </span>
      </span>
      <svg className="brand-waves" viewBox="0 0 35 45" fill="none" aria-hidden="true">
        <path
          d="M3 17c4 5 4 8 0 13M11 10c8 10 8 17 0 26M20 3c13 15 13 25 0 40"
          stroke="currentColor"
          strokeWidth="3.5"
          strokeLinecap="round"
        />
      </svg>
      {preview && <span className="preview-label">Prototipo</span>}
    </Link>
  );
}
