export function readEmailLink() {
  const fragment = new URLSearchParams(window.location.hash.replace(/^#/, ''));
  const result = { userId: fragment.get('userId') ?? '', token: fragment.get('token') ?? '' };
  return result;
}
