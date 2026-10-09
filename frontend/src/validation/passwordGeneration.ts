const PASSWORD_LENGTH = 20;
const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*()-_=+?';
const BYTE_LIMIT = 231;
const MAX_BYTES = 8192;
const MAX_CANDIDATES = 64;
const FAILURE_MESSAGE = 'No pudimos generar una contraseña. Inténtalo de nuevo.';

export function generatePassword(): string {
  try {
    const random = globalThis.crypto;
    if (typeof random?.getRandomValues !== 'function') throw new Error(FAILURE_MESSAGE);
    const bytes = new Uint8Array(64);
    let position = bytes.length;
    let consumed = 0;
    function nextByte(): number {
      if (consumed >= MAX_BYTES) throw new Error(FAILURE_MESSAGE);
      if (position === bytes.length) {
        random.getRandomValues(bytes);
        position = 0;
      }
      consumed += 1;
      return bytes[position++] as number;
    }
    for (let attempt = 0; attempt < MAX_CANDIDATES; attempt += 1) {
      let candidate = '';
      while (candidate.length < PASSWORD_LENGTH) {
        const byte = nextByte();
        // 231 is the largest multiple of 77 below 256: discard the biased remainder.
        if (byte < BYTE_LIMIT) candidate += ALPHABET[byte % ALPHABET.length];
      }
      if (
        /[A-Z]/.test(candidate) &&
        /[a-z]/.test(candidate) &&
        /\d/.test(candidate) &&
        /[^A-Za-z0-9]/.test(candidate)
      )
        return candidate;
    }
    throw new Error(FAILURE_MESSAGE);
  } catch {
    throw new Error(FAILURE_MESSAGE);
  }
}
