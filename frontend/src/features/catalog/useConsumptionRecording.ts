import { useCallback, useEffect, useRef } from 'react';
import { consumptionActivity, type ConsumptionSource } from '../../api/consumptionActivity';
import { ActivityRecorder, type ActivitySample } from './activityRecorder';
export type RecordingWork = { slug: string; version: string };
export function useConsumptionRecording(
  work: RecordingWork | null,
  source: ConsumptionSource,
  read: () => ActivitySample | null,
) {
  const visit = useRef<{ key: string; id: string } | null>(null);
  const recorder = useRef<ActivityRecorder | null>(null);
  const slug = work?.slug;
  const version = work?.version;
  useEffect(() => {
    if (!slug || !version) return;
    const key = source + ':' + slug + ':' + version;
    if (!visit.current || visit.current.key !== key)
      visit.current = { key, id: crypto.randomUUID() };
    const visitId = visit.current.id;
    const controller = new AbortController();
    let current = true;
    let timer: ReturnType<typeof setInterval> | undefined;
    const observe = () => {
      const sample = read();
      if (sample) recorder.current?.observe(sample);
    };
    const visibility = () => {
      const sample = read();
      if (sample) recorder.current?.transition(sample);
    };
    void consumptionActivity
      .capabilities(controller.signal)
      .then((capabilities) => {
        if (!current || !capabilities.recordingEnabled) return;
        const active = new ActivityRecorder(slug, version, visitId, source);
        recorder.current = active;
        observe();
        timer = setInterval(observe, 1000);
        document.addEventListener('visibilitychange', visibility);
        window.addEventListener('focus', visibility);
        window.addEventListener('blur', visibility);
      })
      .catch(() => {
        /* A measurement outage must not prevent access to the work. */
      });
    return () => {
      current = false;
      controller.abort();
      clearInterval(timer);
      document.removeEventListener('visibilitychange', visibility);
      window.removeEventListener('focus', visibility);
      window.removeEventListener('blur', visibility);
      recorder.current?.stop();
      recorder.current = null;
    };
  }, [slug, version, source, read]);
  const transition = useCallback(() => {
    const sample = read();
    if (sample) recorder.current?.transition(sample);
  }, [read]);
  const stop = useCallback(() => recorder.current?.stop(), []);
  return { transition, stop };
}
