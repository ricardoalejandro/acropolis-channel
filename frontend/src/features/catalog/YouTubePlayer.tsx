import { useCallback, useEffect, useRef, useState } from 'react';
import { useConsumptionRecording, type RecordingWork } from './useConsumptionRecording';
import { mediaDuration, mediaMilliseconds, type YouTubeSample } from './activitySampling';
type Player = { destroy: () => void; getPlayerState?: () => number; getCurrentTime?: () => number; getDuration?: () => number; getPlaybackRate?: () => number };
type YouTubeApi = {
  Player: new (
    element: HTMLIFrameElement,
    options: {
      events: {
        onError: (event: { data: number }) => void;
        onStateChange: (event: { data: number }) => void;
        onPlaybackRateChange: () => void;
      };
    },
  ) => Player;
};
declare global {
  interface Window {
    YT?: Partial<YouTubeApi> & { loading?: number; loaded?: number };
    onYouTubeIframeAPIReady?: () => void;
  }
}
let pendingApi: Promise<YouTubeApi> | null = null;
function apiReady(api: Window['YT']): api is YouTubeApi {
  return typeof api?.Player === 'function';
}
function youtubeApi(): Promise<YouTubeApi> {
  if (apiReady(window.YT)) return Promise.resolve(window.YT);
  if (pendingApi) return pendingApi;
  pendingApi = new Promise((resolve, reject) => {
    const previous = window.onYouTubeIframeAPIReady;
    const timer = window.setTimeout(() => {
      pendingApi = null;
      reject(new Error('Player unavailable'));
    }, 15000);
    window.onYouTubeIframeAPIReady = () => {
      previous?.();
      window.clearTimeout(timer);
      if (apiReady(window.YT)) {
        pendingApi = null;
        resolve(window.YT);
      } else {
        pendingApi = null;
        reject(new Error('Player unavailable'));
      }
    };
    const script = document.createElement('script');
    script.src = 'https://www.youtube.com/iframe_api';
    script.referrerPolicy = 'origin';
    script.async = true;
    script.onerror = () => {
      window.clearTimeout(timer);
      pendingApi = null;
      script.remove();
      reject(new Error('Player unavailable'));
    };
    document.head.append(script);
  });
  return pendingApi;
}
export function YouTubePlayer({
  videoId,
  title,
  podcast = false,
  recording = null,
}: {
  videoId: string;
  title: string;
  podcast?: boolean;
  recording?: RecordingWork | null;
}) {
  const [opened, setOpened] = useState(false);
  const [error, setError] = useState(false);
  const [playback, setPlayback] = useState('');
  const frame = useRef<HTMLIFrameElement>(null);
  const activePlayer = useRef<Player | null>(null);
  const read = useCallback((): YouTubeSample | null => {
    const player = activePlayer.current;
    if (!player || typeof player.getPlayerState !== 'function' || typeof player.getCurrentTime !== 'function' || typeof player.getDuration !== 'function' || typeof player.getPlaybackRate !== 'function') return null;
    try {
      const positionMs = mediaMilliseconds(player.getCurrentTime());
      const rate = Math.round(player.getPlaybackRate() * 1000);
      if (positionMs === null || !Number.isSafeInteger(rate) || rate < 250 || rate > 4000) return null;
      const durationMs = mediaDuration(player.getDuration());
      if (durationMs !== null && positionMs > durationMs + 1000) return null;
      const state = player.getPlayerState();
      return { now: performance.now(), visible: document.visibilityState === 'visible', focused: document.hasFocus(), state: state === 1 ? 'playing' : state === 2 ? 'paused' : state === 3 ? 'buffering' : state === 0 ? 'ended' : 'unstarted', positionMs, durationMs, playbackRateMilli: rate };
    } catch { return null; }
  }, []);
  const { transition, stop } = useConsumptionRecording(recording, 'youtube', read);
  useEffect(() => {
    if (!opened) return;
    let alive = true;
    let player: Player | undefined;
    const element = frame.current;
    void youtubeApi()
      .then((api) => {
        if (alive && element)
          activePlayer.current = player = new api.Player(element, {
            events: {
              onError: () => {
                if (alive) { setError(true); stop(); }
              },
              onStateChange: (event) => {
                if (alive)
                  setPlayback(
                    event.data === 1
                      ? 'Reproduciendo'
                      : event.data === 2
                        ? 'En pausa'
                        : event.data === 0
                          ? 'Reproducción finalizada'
                          : '',
                  );
                if (alive) transition();
              },
              onPlaybackRateChange: () => { if (alive) transition(); },
            },
          });
      })
      .catch(() => {
        if (alive) setError(true);
      });
    return () => {
      alive = false;
      activePlayer.current = null;
      if (element && !element.isConnected) player?.destroy();
    };
  }, [opened, videoId, transition, stop]);
  if (!/^[A-Za-z0-9_-]{11}$/.test(videoId))
    return (
      <p className="form-alert" role="alert">
        El vídeo no está disponible.
      </p>
    );
  const query = new URLSearchParams({
    enablejsapi: '1',
    origin: window.location.origin,
    playsinline: '1',
    autoplay: '0',
  });
  return (
    <section
      className="media-work"
      aria-label={podcast ? 'Reproductor de podcast' : 'Reproductor de vídeo'}
    >
      {!opened ? (
        <div className="media-choice">
          <h2>{podcast ? 'Escuchar este podcast' : 'Ver este vídeo'}</h2>
          <p>El reproductor oficial de YouTube se carga sólo cuando lo abres.</p>
          <button className="button" onClick={() => setOpened(true)}>
            {podcast ? 'Reproducir podcast' : 'Reproducir vídeo'}
          </button>
        </div>
      ) : (
        <iframe
          ref={frame}
          className="youtube-player"
          src={'https://www.youtube-nocookie.com/embed/' + videoId + '?' + query}
          title={title}
          width="640"
          height="360"
          referrerPolicy="origin"
          allow="accelerometer; encrypted-media; gyroscope; picture-in-picture; fullscreen"
          allowFullScreen
          onError={() => setError(true)}
        />
      )}
      {playback && !error && (
        <p className="media-playback" role="status">
          {playback}
        </p>
      )}
      {error && (
        <p className="form-alert" role="alert">
          Este vídeo no está disponible para reproducirse aquí. Puedes intentar abrirlo en YouTube.
        </p>
      )}
      <p className="media-origin">
        Este contenido utiliza YouTube.{' '}
        <a
          href={'https://www.youtube.com/watch?v=' + videoId}
          target="_blank"
          rel="noopener noreferrer"
        >
          Abrir en YouTube
        </a>
      </p>
    </section>
  );
}
