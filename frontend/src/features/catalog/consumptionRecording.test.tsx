import { StrictMode, useCallback } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { WorkAccess } from './Work';
import { user as userFixture } from '../../test/fixtures';
import type { ContentDetail } from '../../api/catalog';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { clearCsrf } from '../../api/identity';
import { json } from '../../test/fixtures';
import {
  activityContentId,
  activityNow,
  activityVersion,
} from '../../test/consumptionActivityFixtures';
import { useConsumptionRecording } from './useConsumptionRecording';
import { YouTubePlayer } from './YouTubePlayer';
const sessionState = vi.hoisted(() => ({ signedIn: true }));
vi.mock('../../auth/useSession', () => ({
  useSession: () => ({ user: sessionState.signedIn ? userFixture : null, loading: false }),
}));
const publicDetail: ContentDetail = {
  isFree: true,
  id: activityContentId,
  slug: 'lectura-real',
  title: 'Lectura real',
  category: 'lecturas',
  summary: 'Sinopsis pública',
  body: 'Contexto público',
  coverAsset: null,
  durationSeconds: null,
  publishedUtc: activityNow,
  updatedUtc: activityNow,
  author: null,
  tags: [],
  collectionKind: null,
  items: [],
};
let now = 0;
let tick: (() => void) | undefined;
const nativeSetInterval = globalThis.setInterval;
const nativeClearInterval = globalThis.clearInterval;
const ownedSamplingIntervals = new Set<unknown>();
let currentSamplingInterval: unknown;
let samplingSequence = 0;

let visibility = true;
let focus = true;
let state = -1;
type PlayerEvents = {
  onError: (event: { data: number }) => void;
  onStateChange: (event: { data: number }) => void;
  onPlaybackRateChange: () => void;
};
let events: PlayerEvents | null = null;
class TestOfficialApiShape {
  constructor(_frame: HTMLIFrameElement, options: { events: PlayerEvents }) {
    events = options.events;
  }
  destroy() {}
  getPlayerState() {
    return state;
  }
  getCurrentTime() {
    return now / 1000;
  }
  getDuration() {
    return 60;
  }
  getPlaybackRate() {
    return 1;
  }
}
function ReadingHarness({ version = activityVersion }: { version?: string }) {
  const read = useCallback(
    () => ({
      now,
      visible: visibility,
      focused: focus,
      articleTop: 0,
      articleHeight: 1000,
      viewportHeight: 500,
    }),
    [],
  );
  useConsumptionRecording({ slug: 'lectura-real', version }, 'reading', read);
  return <article>Lectura autorizada</article>;
}
function transport(enabled = true) {
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const path = String(input);
    if (path.endsWith('/consumption/capabilities'))
      return json({
        recordingEnabled: enabled,
        detailRetentionDays: 90,
        generalRetentionDays: 365,
      });
    if (path.endsWith('/csrf')) return json({ token: 'synthetic-csrf' });
    if (path.endsWith('/sessions'))
      return json({
        sessionId: activityContentId,
        startedUtc: activityNow,
        nextSequence: 1,
        sourceKind: path.includes('video') ? 'youtube' : 'reading',
      });
    const packet = JSON.parse(String(init?.body)) as { sequence: number; activeMs: number };
    return json({
      sequence: packet.sequence,
      recordedUtc: activityNow,
      creditedMs: packet.activeMs,
      progressBasisPoints: 5000,
      coverageIncomplete: false,
      endedReported: false,
    });
  });
  vi.stubGlobal('fetch', fetch);
  return fetch;
}
function writes(fetch: ReturnType<typeof transport>) {
  return fetch.mock.calls.filter(([, init]) => init?.method === 'POST');
}
async function sample() {
  now += 1000;
  await act(async () => {
    tick?.();
  });
}
beforeEach(() => {
  now = 0;
  tick = undefined;
  currentSamplingInterval = undefined;
  ownedSamplingIntervals.clear();
  visibility = true;
  focus = true;
  state = -1;
  events = null;
  sessionState.signedIn = true;
  clearCsrf();
  vi.spyOn(performance, 'now').mockImplementation(() => now);
  vi.spyOn(globalThis, 'setInterval').mockImplementation((...args) => {
    const [callback, delay] = args;
    // Control only the production sampler; Testing Library polling uses real timers.
    if (delay !== 1000)
      return Reflect.apply(nativeSetInterval, globalThis, args) as ReturnType<typeof setInterval>;
    const handle = (1000000000 + ++samplingSequence) as unknown as ReturnType<typeof setInterval>;
    ownedSamplingIntervals.add(handle);
    currentSamplingInterval = handle;
    tick = callback as () => void;
    return handle;
  });
  vi.spyOn(globalThis, 'clearInterval').mockImplementation((handle) => {
    if (ownedSamplingIntervals.has(handle)) {
      ownedSamplingIntervals.delete(handle);
      if (handle === currentSamplingInterval) {
        currentSamplingInterval = undefined;
        tick = undefined;
      }
      return;
    }
    nativeClearInterval(handle);
  });
  vi.spyOn(document, 'hasFocus').mockReturnValue(true);
  vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
  window.YT = { Player: TestOfficialApiShape };
});
afterEach(() => {
  cleanup();
  delete window.YT;
});
describe('availability and actual work signal integration', () => {
  it('never opens a visit or installs sampling when recording is disabled', async () => {
    const fetch = transport(false);
    await act(async () => {
      render(<ReadingHarness />);
    });
    await waitFor(() => expect(fetch).toHaveBeenCalledOnce());
    for (let i = 0; i < 12; i++) await sample();
    expect(writes(fetch)).toHaveLength(0);
    expect(tick).toBeUndefined();
    expect(screen.getByText('Lectura autorizada')).toBeVisible();
  });
  it('uses a stable idempotent visit under StrictMode with visible authorized reading', async () => {
    const fetch = transport();
    await act(async () => {
      render(
        <StrictMode>
          <ReadingHarness />
        </StrictMode>,
      );
    });
    await waitFor(() => expect(writes(fetch)).toHaveLength(1), { timeout: 3000 });
    const body = JSON.parse(String(writes(fetch)[0]?.[1]?.body));
    expect(body).toEqual({
      visitId: expect.stringMatching(/^[0-9a-f-]{36}$/),
      contentVersion: activityVersion,
    });
    for (let i = 0; i < 10; i++) await sample();
    await waitFor(() => expect(writes(fetch)).toHaveLength(2));
    expect(JSON.parse(String(writes(fetch)[1]?.[1]?.body))).toHaveProperty('reading');
  });
  it('does not start a hidden reading and begins only after visible exposure', async () => {
    visibility = false;
    const fetch = transport();
    await act(async () => {
      render(<ReadingHarness />);
    });
    await waitFor(() => expect(tick).toBeDefined());
    expect(writes(fetch)).toHaveLength(0);
    visibility = true;
    await sample();
    await waitFor(() => expect(writes(fetch)).toHaveLength(1));
  });
  it('does not open a visit for iframe load, READY, or a paused YouTube state', async () => {
    const fetch = transport();
    await act(async () => {
      render(
        <YouTubePlayer
          videoId="M7lc1UVf-VE"
          title="Vídeo real"
          recording={{ slug: 'video-real', version: activityVersion }}
        />,
      );
    });
    await waitFor(() => expect(tick).toBeDefined());
    expect(document.querySelector('iframe')).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Reproducir vídeo' }));
    await waitFor(() => expect(document.querySelector('iframe')).not.toBeNull());
    await waitFor(() => expect(events).not.toBeNull());
    await act(async () => {
      state = -1;
      events!.onStateChange({ data: -1 });
    });
    await sample();
    await act(async () => {
      state = 2;
      events!.onStateChange({ data: 2 });
    });
    await sample();
    expect(writes(fetch)).toHaveLength(0);
    expect(document.querySelector('iframe')?.getAttribute('src')).toContain('autoplay=0');
  });
  it('starts only after official PLAYING and submits sampled media without claiming real external playback', async () => {
    const fetch = transport();
    await act(async () => {
      render(
        <YouTubePlayer
          videoId="M7lc1UVf-VE"
          title="Vídeo real"
          recording={{ slug: 'video-real', version: activityVersion }}
        />,
      );
    });
    await waitFor(() => expect(tick).toBeDefined());
    await userEvent.click(screen.getByRole('button', { name: 'Reproducir vídeo' }));
    await waitFor(() => expect(document.querySelector('iframe')).not.toBeNull());
    await waitFor(() => expect(events).not.toBeNull());
    await act(async () => {
      state = 1;
      events!.onStateChange({ data: 1 });
    });
    await waitFor(() => expect(writes(fetch)).toHaveLength(1));
    for (let i = 0; i < 10; i++) await sample();
    await waitFor(() => expect(writes(fetch)).toHaveLength(2));
    const packet = JSON.parse(String(writes(fetch)[1]?.[1]?.body));
    expect(packet).toMatchObject({
      sequence: 1,
      media: { state: 'playing', durationMs: 60000, playbackRateMilli: 1000 },
    });
    expect(packet).not.toHaveProperty('reading');
    expect(screen.getByRole('status')).toHaveTextContent('Reproduciendo');
  });
  it('does not add consumption traffic to a standalone player without authorized work context', async () => {
    const fetch = transport();
    await act(async () => {
      render(<YouTubePlayer videoId="M7lc1UVf-VE" title="Vídeo sin contexto" />);
    });
    await userEvent.click(screen.getByRole('button', { name: 'Reproducir vídeo' }));
    await waitFor(() => expect(document.querySelector('iframe')).not.toBeNull());
    await waitFor(() => expect(events).not.toBeNull());
    await act(async () => {
      state = 1;
      events!.onStateChange({ data: 1 });
    });
    for (let i = 0; i < 12; i++) await sample();
    expect(fetch).not.toHaveBeenCalled();
  });
});

describe('authorized work boundary before recording', () => {
  it('does not probe recording or start a visit from a public anonymous synopsis', async () => {
    sessionState.signedIn = false;
    const fetch = transport();
    await act(async () => {
      render(
        <MemoryRouter>
          <WorkAccess item={publicDetail} />
        </MemoryRouter>,
      );
    });
    expect(screen.getByRole('heading', { name: 'Accede a la obra completa.' })).toBeVisible();
    expect(fetch).not.toHaveBeenCalled();
    expect(tick).toBeUndefined();
  });
  it('does not probe recording or record a work that the consumption endpoint rejects', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      json({ code: 'subscription_required' }, 403),
    );
    vi.stubGlobal('fetch', fetch);
    await act(async () => {
      render(
        <MemoryRouter>
          <WorkAccess item={publicDetail} />
        </MemoryRouter>,
      );
    });
    await screen.findByRole('heading', { name: 'Revisa tu suscripción.' });
    expect(fetch).toHaveBeenCalledOnce();
    expect(String(fetch.mock.calls[0]?.[0])).toContain('/consumption/content/lectura-real');
    expect(tick).toBeUndefined();
  });
  it('records only after the protected reading is authorized and actually exposed', async () => {
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({
      x: 0,
      y: 0,
      top: 0,
      bottom: 1000,
      left: 0,
      right: 500,
      width: 500,
      height: 1000,
      toJSON: () => ({}),
    });
    const delegate = transport();
    const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) =>
      String(input).endsWith('/consumption/content/lectura-real')
        ? json({
            id: activityContentId,
            slug: 'lectura-real',
            title: 'Lectura real',
            category: 'lecturas',
            version: activityVersion,
            workText: '<script>literal()</script>',
            youTubeId: null,
            collectionKind: null,
            items: [],
          })
        : delegate(input, init),
    );
    vi.stubGlobal('fetch', fetch);
    await act(async () => {
      render(
        <MemoryRouter>
          <WorkAccess item={publicDetail} />
        </MemoryRouter>,
      );
    });
    await screen.findByRole('article', { name: 'Lectura completa' });
    await waitFor(() => expect(writes(delegate)).toHaveLength(1));
    expect(screen.getByText('<script>literal()</script>')).toBeVisible();
    expect(document.querySelector('script')).toBeNull();
    expect(String(fetch.mock.calls[0]?.[0])).toContain('/consumption/content/lectura-real');
  });
  it('does not turn an authorized course navigation into a reading visit', async () => {
    const fetch = vi.fn<typeof globalThis.fetch>(async () =>
      json({
        id: activityContentId,
        slug: 'curso-real',
        title: 'Curso real',
        category: 'cursos',
        version: activityVersion,
        workText: null,
        youTubeId: null,
        collectionKind: 'course',
        items: [],
      }),
    );
    vi.stubGlobal('fetch', fetch);
    await act(async () => {
      render(
        <MemoryRouter>
          <WorkAccess
            item={{
              ...publicDetail,
              slug: 'curso-real',
              category: 'cursos',
              collectionKind: 'course',
            }}
          />
        </MemoryRouter>,
      );
    });
    await screen.findByText('Tu acceso a este curso está activo. Elige un contenido de la lista.');
    expect(fetch).toHaveBeenCalledOnce();
    expect(tick).toBeUndefined();
  });
});

describe('visit context identity', () => {
  it('creates a new visit when the authorized work version changes while preserving StrictMode idempotency', async () => {
    const fetch = transport();
    let mounted!: ReturnType<typeof render>;
    await act(async () => {
      mounted = render(<ReadingHarness />);
    });
    await waitFor(() => expect(writes(fetch)).toHaveLength(1));
    const nextVersion = 'abcdef1234567890abcdef1234567890';
    await act(async () => {
      mounted.rerender(<ReadingHarness version={nextVersion} />);
    });
    await waitFor(() => expect(writes(fetch)).toHaveLength(2));
    const first = JSON.parse(String(writes(fetch)[0]?.[1]?.body));
    const second = JSON.parse(String(writes(fetch)[1]?.[1]?.body));
    expect(second.contentVersion).toBe(nextVersion);
    expect(first.contentVersion).toBe(activityVersion);
    expect(second.visitId).not.toBe(first.visitId);
  });
});
