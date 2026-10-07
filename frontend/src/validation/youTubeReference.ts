import { hasControlCharacters } from './plainText';

export const MAX_YOUTUBE_REFERENCE_LENGTH = 2048;
const videoIdPattern = /^[A-Za-z0-9_-]{11}$/;
const youTubeHosts = new Set(['youtube.com', 'www.youtube.com', 'm.youtube.com', 'youtu.be']);

export function normalizeYouTubeReference(input: string): string | null {
  if (input.length > MAX_YOUTUBE_REFERENCE_LENGTH || hasControlCharacters(input)) return null;
  const value = input.trim();
  if (videoIdPattern.test(value)) return value;
  if (/[<>\\]/.test(value)) return null;
  const authority = /^https:\/\/([^/?#]+)(?=\/|\?|#|$)/i.exec(value)?.[1]?.toLowerCase();
  if (!authority || !youTubeHosts.has(authority)) return null;
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return null;
  }
  if (
    url.protocol !== 'https:' ||
    url.hostname !== authority ||
    url.username ||
    url.password ||
    url.port
  )
    return null;
  const path = value.slice(value.indexOf('://') + 3 + authority.length).split(/[?#]/)[0] || '/';
  if (url.pathname !== path) return null;
  if (authority === 'youtu.be') return /^\/([A-Za-z0-9_-]{11})\/?$/.exec(path)?.[1] ?? null;
  if (path === '/watch' || path === '/watch/') {
    const ids = url.searchParams.getAll('v');
    return ids.length === 1 && videoIdPattern.test(ids[0]!) ? ids[0]! : null;
  }
  return /^\/(?:embed|shorts|live)\/([A-Za-z0-9_-]{11})\/?$/.exec(path)?.[1] ?? null;
}
