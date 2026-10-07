import { describe, expect, it } from 'vitest';
import {
  MAX_SAMPLE_GAP_MS, MAX_MEDIA_MS, mergeObservedRanges, readingExposure, readingStep,
  youtubeStep, mediaMilliseconds, mediaDuration, type ReadingSample, type YouTubeSample,
} from './activitySampling';
const reading = (overrides: Partial<ReadingSample> = {}): ReadingSample => ({
  now: 1000, visible: true, focused: true, articleTop: 0, articleHeight: 2000, viewportHeight: 500,
  ...overrides,
});
const video = (overrides: Partial<YouTubeSample> = {}): YouTubeSample => ({
  now: 1000, visible: true, focused: true, state: 'playing', positionMs: 1000,
  durationMs: 60000, playbackRateMilli: 1000, ...overrides,
});
describe('reading exposure samples', () => {
  it('reports only the viewport range, separate from elapsed time', () => {
    expect(readingExposure(reading())).toEqual({ exposedRanges: [{ from: 0, to: 2500 }], positionBasisPoints: 2500 });
    expect(readingStep(null, reading()).activeMs).toBe(0);
  });
  it('reports a short visible text as exposed without claiming comprehension', () => {
    expect(readingExposure(reading({ articleHeight: 300 }))).toEqual({ exposedRanges: [{ from: 0, to: 10000 }], positionBasisPoints: 10000 });
  });
  it('keeps a scrolled range rather than filling the intervening text', () => {
    expect(readingExposure(reading({ articleTop: -1200 }))).toEqual({ exposedRanges: [{ from: 6000, to: 8500 }], positionBasisPoints: 8500 });
  });
  it('clips the final viewport to the article', () => {
    expect(readingExposure(reading({ articleTop: -1800 }))).toEqual({ exposedRanges: [{ from: 9000, to: 10000 }], positionBasisPoints: 10000 });
  });
  it.each([{ articleTop: 600 }, { articleTop: -2100 }, { articleHeight: 0 }, { articleHeight: Infinity }, { viewportHeight: 0 }, { articleTop: NaN }])('rejects non-visible or invalid geometry %j', (override) => {
    expect(readingExposure(reading(override))).toEqual({ exposedRanges: [], positionBasisPoints: 0 });
  });
  it.each([{ visible: false }, { focused: false }])('does not expose hidden or unfocused text %j', (override) => {
    expect(readingExposure(reading(override)).exposedRanges).toEqual([]);
  });
  it('credits a bounded interval only when both samples expose text', () => {
    expect(readingStep(reading({ now: 0 }), reading()).activeMs).toBe(1000);
    expect(readingStep(reading({ now: 0, visible: false }), reading()).activeMs).toBe(0);
    expect(readingStep(reading({ now: 0 }), reading({ focused: false })).activeMs).toBe(0);
  });
  it('does not fill a scheduler gap or a regressing clock', () => {
    expect(readingStep(reading({ now: 0 }), reading({ now: MAX_SAMPLE_GAP_MS + 1 })).activeMs).toBe(0);
    expect(readingStep(reading({ now: 2000 }), reading()).activeMs).toBe(0);
  });
  it('recalculates exposure after resize without interpolating geometry', () => {
    expect(readingExposure(reading({ articleHeight: 1000, viewportHeight: 500 })).exposedRanges).toEqual([{ from: 0, to: 5000 }]);
    expect(readingExposure(reading({ articleHeight: 3000, viewportHeight: 300 })).exposedRanges).toEqual([{ from: 0, to: 1000 }]);
  });
});
describe('YouTube observed continuity', () => {
  it('does not count the first state as elapsed playback', () => {
    expect(youtubeStep(null, video())).toEqual({ activeMs: 0, segments: [], endedReported: false });
  });
  it('credits observed forward playback', () => {
    expect(youtubeStep(video({ now: 0, positionMs: 0 }), video())).toEqual({ activeMs: 1000, segments: [{ from: 0, to: 1000 }], endedReported: false });
  });
  it.each(['paused', 'buffering', 'ended', 'unstarted'] as const)('does not credit the %s state', (state) => {
    expect(youtubeStep(video({ now: 0, positionMs: 0 }), video({ state })).activeMs).toBe(0);
  });
  it('reports ENDED separately without forcing coverage', () => {
    expect(youtubeStep(video({ now: 0, positionMs: 0 }), video({ state: 'ended', positionMs: 60000 }))).toEqual({ activeMs: 0, segments: [], endedReported: true });
  });
  it.each([{ visible: false }, { focused: false }])('does not credit hidden or unfocused playback %j', (override) => {
    expect(youtubeStep(video({ now: 0, positionMs: 0 }), video(override)).activeMs).toBe(0);
    expect(youtubeStep(video({ now: 0, positionMs: 0, ...override }), video()).activeMs).toBe(0);
  });
  it('rejects a forward seek and never joins the skipped media', () => {
    expect(youtubeStep(video({ now: 0, positionMs: 0 }), video({ positionMs: 10000 })).segments).toEqual([]);
  });
  it('rejects a backward seek and a stationary clock', () => {
    expect(youtubeStep(video({ now: 0, positionMs: 2000 }), video()).segments).toEqual([]);
    expect(youtubeStep(video({ now: 0 }), video()).activeMs).toBe(0);
  });
  it('starts a new segment after a playback-rate change', () => {
    expect(youtubeStep(video({ now: 0, positionMs: 0 }), video({ playbackRateMilli: 2000, positionMs: 2000 })).segments).toEqual([]);
  });
  it('bounds doubled-speed media by its elapsed time', () => {
    expect(youtubeStep(video({ now: 0, positionMs: 0, playbackRateMilli: 2000 }), video({ positionMs: 2000, playbackRateMilli: 2000 }))).toEqual({ activeMs: 1000, segments: [{ from: 0, to: 2000 }], endedReported: false });
  });
  it('caps sampled clock rounding without crediting its excess', () => {
    expect(youtubeStep(video({ now: 0, positionMs: 0 }), video({ positionMs: 1100 }))).toEqual({ activeMs: 1000, segments: [{ from: 0, to: 1000 }], endedReported: false });
  });
  it('credits less time when less forward media is actually observed', () => {
    expect(youtubeStep(video({ now: 0, positionMs: 0 }), video({ positionMs: 800 })).activeMs).toBe(800);
  });
  it('preserves observed continuity with unknown or changing duration', () => {
    expect(youtubeStep(video({ now: 0, positionMs: 0, durationMs: null }), video({ durationMs: 70000 })).segments).toEqual([{ from: 0, to: 1000 }]);
  });
  it.each([{ positionMs: -1 }, { positionMs: Infinity }, { positionMs: MAX_MEDIA_MS + 1 }, { playbackRateMilli: 249 }, { playbackRateMilli: 4001 }])('rejects invalid positions or rates %j', (override) => {
    expect(youtubeStep(video({ now: 0, positionMs: 0 }), video(override)).activeMs).toBe(0);
  });
  it('does not fill delayed or regressing sample time', () => {
    expect(youtubeStep(video({ now: 0, positionMs: 0 }), video({ now: MAX_SAMPLE_GAP_MS + 1 })).activeMs).toBe(0);
    expect(youtubeStep(video({ now: 2000 }), video()).activeMs).toBe(0);
  });
});
describe('observed range and media normalization', () => {
  it('unions overlap and adjacency without duplicating coverage', () => {
    const ranges = [{ from: 20, to: 40 }, { from: 0, to: 20 }, { from: 10, to: 30 }, { from: 50, to: 60 }];
    expect(mergeObservedRanges(ranges)).toEqual([{ from: 0, to: 40 }, { from: 50, to: 60 }]);
    expect(ranges[0]).toEqual({ from: 20, to: 40 });
  });
  it('does not bridge separated ranges or retain invalid geometry', () => {
    expect(mergeObservedRanges([{ from: 0, to: 10 }, { from: 20, to: 30 }, { from: -1, to: 1 }, { from: 1, to: Infinity }, { from: 4, to: 4 }])).toEqual([{ from: 0, to: 10 }, { from: 20, to: 30 }]);
  });
  it('keeps repeated media coverage as one union while time remains independent', () => {
    expect(mergeObservedRanges([{ from: 0, to: 1000 }, { from: 0, to: 1000 }])).toEqual([{ from: 0, to: 1000 }]);
  });
  it('converts finite samples to bounded integer milliseconds', () => {
    expect(mediaMilliseconds(1.2349)).toBe(1234);
    expect(mediaMilliseconds(0)).toBe(0);
    expect(mediaMilliseconds(86400)).toBe(MAX_MEDIA_MS);
    expect(mediaMilliseconds(86400.1)).toBeNull();
    expect(mediaMilliseconds(NaN)).toBeNull();
    expect(mediaMilliseconds(-1)).toBeNull();
  });
  it('keeps zero, unknown and invalid duration indeterminate', () => {
    expect(mediaDuration(0)).toBeNull();
    expect(mediaDuration(Infinity)).toBeNull();
    expect(mediaDuration(60)).toBe(60000);
  });
});

describe('known duration segment limits', () => {
  it('clips rounding at the media end and credits only the bounded segment', () => {
    expect(youtubeStep(video({ now: 0, positionMs: 59000 }), video({ positionMs: 60100 }))).toEqual({ activeMs: 1000, segments: [{ from: 59000, to: 60000 }], endedReported: false });
    expect(youtubeStep(video({ now: 0, positionMs: 59500 }), video({ positionMs: 60500 }))).toEqual({ activeMs: 500, segments: [{ from: 59500, to: 60000 }], endedReported: false });
  });
  it('never submits segments beginning after the known duration', () => {
    expect(youtubeStep(video({ now: 0, positionMs: 60100 }), video({ positionMs: 61100 }))).toEqual({ activeMs: 0, segments: [], endedReported: false });
    expect(youtubeStep(video({ now: 0, positionMs: 59999, playbackRateMilli: 4000 }), video({ positionMs: 60001, playbackRateMilli: 4000 }))).toEqual({ activeMs: 0, segments: [], endedReported: false });
  });
});
