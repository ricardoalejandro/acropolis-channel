export const MAX_SAMPLE_GAP_MS = 1500;
export const MAX_MEDIA_MS = 86400000;
export type ObservedRange = { from: number; to: number };
export type Visibility = { visible: boolean; focused: boolean };
export type ReadingSample = Visibility & {
  now: number;
  articleTop: number;
  articleHeight: number;
  viewportHeight: number;
};
export type YouTubeState = 'playing' | 'paused' | 'buffering' | 'ended' | 'unstarted';
export type YouTubeSample = Visibility & {
  now: number;
  state: YouTubeState;
  positionMs: number;
  durationMs: number | null;
  playbackRateMilli: number;
};
function finite(value: number) {
  return Number.isFinite(value);
}
function boundedInteger(value: number, maximum: number) {
  return Number.isSafeInteger(value) && value >= 0 && value <= maximum;
}
function interval(previous: { now: number } | null, now: number) {
  if (!previous || !finite(now) || !finite(previous.now)) return 0;
  const elapsed = now - previous.now;
  return elapsed > 0 && elapsed <= MAX_SAMPLE_GAP_MS ? Math.floor(elapsed) : 0;
}
export function mergeObservedRanges(ranges: readonly ObservedRange[]): ObservedRange[] {
  const ordered = ranges
    .filter(
      (range) => finite(range.from) && finite(range.to) && range.from >= 0 && range.to > range.from,
    )
    .map((range) => ({ ...range }))
    .sort((a, b) => a.from - b.from || a.to - b.to);
  const result: ObservedRange[] = [];
  for (const range of ordered) {
    const last = result.at(-1);
    if (last && range.from <= last.to) last.to = Math.max(last.to, range.to);
    else result.push(range);
  }
  return result;
}
export function readingExposure(sample: ReadingSample): {
  exposedRanges: ObservedRange[];
  positionBasisPoints: number;
} {
  if (
    !sample.visible ||
    !sample.focused ||
    !finite(sample.articleTop) ||
    !finite(sample.articleHeight) ||
    sample.articleHeight <= 0 ||
    !finite(sample.viewportHeight) ||
    sample.viewportHeight <= 0
  )
    return { exposedRanges: [], positionBasisPoints: 0 };
  const start = Math.max(0, -sample.articleTop);
  const end = Math.min(sample.articleHeight, sample.viewportHeight - sample.articleTop);
  if (end <= start) return { exposedRanges: [], positionBasisPoints: 0 };
  const from = Math.ceil((start * 10000) / sample.articleHeight);
  const to = Math.floor((end * 10000) / sample.articleHeight);
  return { exposedRanges: from < to ? [{ from, to }] : [], positionBasisPoints: to };
}
export function readingStep(previous: ReadingSample | null, current: ReadingSample) {
  const exposure = readingExposure(current);
  const wasExposed = previous !== null && readingExposure(previous).exposedRanges.length > 0;
  return {
    ...exposure,
    activeMs: wasExposed && exposure.exposedRanges.length > 0 ? interval(previous, current.now) : 0,
  };
}
export function youtubeStep(
  previous: YouTubeSample | null,
  current: YouTubeSample,
): {
  activeMs: number;
  segments: ObservedRange[];
  endedReported: boolean;
} {
  const empty = {
    activeMs: 0,
    segments: [] as ObservedRange[],
    endedReported: current.state === 'ended',
  };
  const elapsed = interval(previous, current.now);
  if (
    !previous ||
    !elapsed ||
    !current.visible ||
    !current.focused ||
    !previous.visible ||
    !previous.focused ||
    previous.state !== 'playing' ||
    current.state !== 'playing' ||
    !boundedInteger(previous.positionMs, MAX_MEDIA_MS) ||
    !boundedInteger(current.positionMs, MAX_MEDIA_MS) ||
    !Number.isSafeInteger(current.playbackRateMilli) ||
    current.playbackRateMilli < 250 ||
    current.playbackRateMilli > 4000 ||
    previous.playbackRateMilli !== current.playbackRateMilli
  )
    return empty;
  const advance = current.positionMs - previous.positionMs;
  const maximumAdvance = (elapsed * current.playbackRateMilli) / 1000;
  // API positions are samples: tolerate bounded clock rounding, never credit that excess.
  if (advance <= 0 || advance > maximumAdvance + 250) return empty;
  const activeMs = Math.min(elapsed, Math.floor((advance * 1000) / current.playbackRateMilli));
  const to = Math.min(
    current.durationMs ?? MAX_MEDIA_MS,
    current.positionMs,
    previous.positionMs + Math.floor((activeMs * current.playbackRateMilli) / 1000),
  );
  if (activeMs <= 0 || to <= previous.positionMs) return empty;
  const boundedCredit = Math.min(
    activeMs,
    Math.floor(((to - previous.positionMs) * 1000) / current.playbackRateMilli),
  );
  if (boundedCredit <= 0) return empty;
  return {
    activeMs: boundedCredit,
    segments: [{ from: previous.positionMs, to }],
    endedReported: false,
  };
}
export function mediaMilliseconds(seconds: number): number | null {
  if (!finite(seconds) || seconds < 0 || seconds > MAX_MEDIA_MS / 1000) return null;
  return Math.floor(seconds * 1000);
}
export function mediaDuration(seconds: number): number | null {
  const value = mediaMilliseconds(seconds);
  return value !== null && value > 0 ? value : null;
}
