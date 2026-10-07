import { describe, expect, it } from 'vitest';
import { MAX_YOUTUBE_REFERENCE_LENGTH, normalizeYouTubeReference } from './youTubeReference';

describe('YouTube editorial references', () => {
  it.each([
    ['existing identifier', 'M7lc1UVf-VE'],
    ['identifier with surrounding spaces', '  M7lc1UVf-VE  '],
    ['watch URL', 'https://www.youtube.com/watch?v=M7lc1UVf-VE'],
    ['watch URL without www', 'https://youtube.com/watch?v=M7lc1UVf-VE'],
    ['mobile watch URL', 'https://m.youtube.com/watch?v=M7lc1UVf-VE'],
    [
      'watch sharing parameters',
      'https://www.youtube.com/watch?si=share&v=M7lc1UVf-VE&t=90&list=PL-test',
    ],
    ['watch trailing slash', 'https://www.youtube.com/watch/?v=M7lc1UVf-VE'],
    ['short sharing URL', 'https://youtu.be/M7lc1UVf-VE?si=share&t=12'],
    ['short URL trailing slash', 'https://youtu.be/M7lc1UVf-VE/'],
    ['embed URL', 'https://www.youtube.com/embed/M7lc1UVf-VE?start=12'],
    ['shorts URL', 'https://www.youtube.com/shorts/M7lc1UVf-VE?feature=share'],
    ['live URL', 'https://www.youtube.com/live/M7lc1UVf-VE?si=share'],
    ['uppercase authority', 'HTTPS://WWW.YOUTUBE.COM/watch?v=M7lc1UVf-VE'],
    ['URL with surrounding spaces', '  https://youtu.be/M7lc1UVf-VE  '],
  ])('normalizes %s to the existing ID-only contract', (_name, input) => {
    expect(normalizeYouTubeReference(input)).toBe('M7lc1UVf-VE');
  });

  it.each([
    ['empty', ''],
    ['only spaces', '   '],
    ['short identifier', 'M7lc1UVf-V'],
    ['long identifier', 'M7lc1UVf-VE0'],
    ['trailing control', 'M7lc1UVf-VE\n'],
    ['embedded control', 'https://www.youtube.com/watch?v=M7lc1UVf-VE\t'],
    ['HTTP', 'http://www.youtube.com/watch?v=M7lc1UVf-VE'],
    ['scheme relative', '//www.youtube.com/watch?v=M7lc1UVf-VE'],
    ['no scheme', 'www.youtube.com/watch?v=M7lc1UVf-VE'],
    ['foreign origin', 'https://example.test/watch?v=M7lc1UVf-VE'],
    ['spoofed subdomain', 'https://youtube.com.example.test/watch?v=M7lc1UVf-VE'],
    ['unapproved subdomain', 'https://studio.youtube.com/watch?v=M7lc1UVf-VE'],
    ['user info', 'https://admin@www.youtube.com/watch?v=M7lc1UVf-VE'],
    ['password info', 'https://admin:pass@www.youtube.com/watch?v=M7lc1UVf-VE'],
    ['explicit TLS port', 'https://www.youtube.com:443/watch?v=M7lc1UVf-VE'],
    ['other port', 'https://www.youtube.com:8443/watch?v=M7lc1UVf-VE'],
    ['host trailing dot', 'https://www.youtube.com./watch?v=M7lc1UVf-VE'],
    ['missing identifier', 'https://www.youtube.com/watch'],
    ['empty identifier', 'https://www.youtube.com/watch?v='],
    ['duplicate identifier', 'https://www.youtube.com/watch?v=M7lc1UVf-VE&v=38Oq_C4AxgA'],
    ['partial URL identifier', 'https://youtu.be/M7lc1UVf-V'],
    ['extra path', 'https://youtu.be/M7lc1UVf-VE/extra'],
    ['channel URL', 'https://www.youtube.com/@channel'],
    ['playlist URL', 'https://www.youtube.com/playlist?list=PL-test'],
    ['dot path normalization', 'https://www.youtube.com/extra/../embed/M7lc1UVf-VE'],
    ['encoded path', 'https://www.youtube.com/embed/%4d7lc1UVf-VE'],
    ['backslash normalization', 'https://www.youtube.com\\embed\\M7lc1UVf-VE'],
    ['iframe markup', '<iframe src="https://www.youtube.com/embed/M7lc1UVf-VE"></iframe>'],
    ['markup in query', 'https://www.youtube.com/watch?v=M7lc1UVf-VE&x=<script>'],
    ['javascript scheme', 'javascript:alert(1)'],
    ['data scheme', 'data:text/html,<iframe>'],
  ])('rejects %s', (_name, input) => {
    expect(normalizeYouTubeReference(input)).toBeNull();
  });

  it('accepts a sharing URL at the input limit without preserving its parameters', () => {
    const prefix = 'https://youtu.be/M7lc1UVf-VE?si=';
    const input = prefix + 'a'.repeat(MAX_YOUTUBE_REFERENCE_LENGTH - prefix.length);
    expect(input).toHaveLength(2048);
    expect(normalizeYouTubeReference(input)).toBe('M7lc1UVf-VE');
  });

  it('rejects an oversized URL even when its video ID is valid', () => {
    const prefix = 'https://youtu.be/M7lc1UVf-VE?si=';
    const input = prefix + 'a'.repeat(MAX_YOUTUBE_REFERENCE_LENGTH - prefix.length + 1);
    expect(normalizeYouTubeReference(input)).toBeNull();
  });
});
