import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../../api/identity';
import { ActivityRecorder, type ActivityTransport } from './activityRecorder';
import type {
  ConsumptionPulse,
  ConsumptionReceipt,
  ConsumptionSession,
} from '../../api/consumptionActivity';
import type { ReadingSample, YouTubeSample } from './activitySampling';
const sessionId = '20000000-0000-0000-0000-000000000002';
const visitId = '10000000-0000-0000-0000-000000000001';
const version = '1234567890abcdef1234567890abcdef';
let now = 0;
const reading = (): ReadingSample => ({
  now,
  visible: true,
  focused: true,
  articleTop: 0,
  articleHeight: 1000,
  viewportHeight: 500,
});
const video = (state: YouTubeSample['state'] = 'playing'): YouTubeSample => ({
  now,
  visible: true,
  focused: true,
  state,
  positionMs: now,
  durationMs: 60000,
  playbackRateMilli: 1000,
});
const receipt = (packet: ConsumptionPulse): ConsumptionReceipt => ({
  sequence: packet.sequence,
  recordedUtc: '2026-10-06T12:00:00Z',
  creditedMs: packet.activeMs,
  progressBasisPoints: 5000,
  coverageIncomplete: false,
  endedReported: 'media' in packet && packet.media?.state === 'ended',
});
const settle = async () => {
  await Promise.resolve();
  await Promise.resolve();
};
function setup(source: 'reading' | 'youtube' = 'reading') {
  const start = vi.fn<ActivityTransport['start']>(async (): Promise<ConsumptionSession> => ({
    sessionId,
    startedUtc: '2026-10-06T12:00:00Z',
    nextSequence: 1,
    sourceKind: source,
  }));
  const pulse = vi.fn<ActivityTransport['pulse']>(
    async (_id: string, body: ConsumptionPulse): Promise<ConsumptionReceipt> => receipt(body),
  );
  const recorder = new ActivityRecorder(
    'obra',
    version,
    visitId,
    source,
    { start, pulse },
    () => now,
  );
  return { start, pulse, recorder };
}
function ticks(recorder: ActivityRecorder, from: number, to: number) {
  for (now = from; now <= to; now += 1000) recorder.observe(reading());
  now = to;
}
beforeEach(() => {
  now = 0;
});
describe('serialized observed activity transport', () => {
  it('uses one idempotent visit and no duplicate opening while the first request is pending', async () => {
    const context = setup();
    for (let i = 0; i < 8; i++) context.recorder.observe(reading());
    await settle();
    expect(context.start).toHaveBeenCalledTimes(1);
    expect(context.start).toHaveBeenCalledWith('obra', visitId, version, expect.any(AbortSignal));
    expect(context.pulse).not.toHaveBeenCalled();
    context.recorder.stop();
  });
  it('sends a reading-only packet and advances the sequence from its accepted receipt', async () => {
    const context = setup();
    context.recorder.observe(reading());
    await settle();
    ticks(context.recorder, 1000, 10000);
    await settle();
    expect(context.pulse).toHaveBeenCalledTimes(1);
    expect(context.pulse.mock.calls[0]?.[1]).toEqual({
      sequence: 1,
      intervalMs: 10000,
      activeMs: 9000,
      reading: { positionBasisPoints: 5000, exposedRanges: [{ from: 0, to: 5000 }] },
    });
    ticks(context.recorder, 11000, 20000);
    await settle();
    expect(context.pulse.mock.calls[1]?.[1].sequence).toBe(2);
    context.recorder.stop();
  });
  it('keeps one request in flight and discards intervals while acknowledgement is pending', async () => {
    const context = setup();
    let accept!: (value: ConsumptionReceipt) => void;
    context.pulse.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          accept = resolve;
        }),
    );
    context.recorder.observe(reading());
    await settle();
    ticks(context.recorder, 1000, 10000);
    const first = context.pulse.mock.calls[0]![1];
    ticks(context.recorder, 11000, 20000);
    expect(context.pulse).toHaveBeenCalledTimes(1);
    accept(receipt(first));
    await settle();
    ticks(context.recorder, 21000, 30000);
    await settle();
    expect(context.pulse.mock.calls[1]?.[1]).toMatchObject({
      sequence: 2,
      intervalMs: 10000,
      activeMs: 9000,
    });
    context.recorder.stop();
  });
  it('retries exactly the same envelope after 503 without generating a new sequence', async () => {
    const context = setup();
    context.pulse.mockRejectedValueOnce(new ApiError(503, 'unavailable'));
    context.recorder.observe(reading());
    await settle();
    ticks(context.recorder, 1000, 10000);
    await settle();
    const packet = context.pulse.mock.calls[0]![1];
    now = 19999;
    context.recorder.observe(reading());
    expect(context.pulse).toHaveBeenCalledTimes(1);
    now = 20000;
    context.recorder.observe(reading());
    await settle();
    expect(context.pulse.mock.calls[1]?.[1]).toBe(packet);
    context.recorder.stop();
  });
  it('waits a full minute after 429 and does not burst during the cooldown', async () => {
    const context = setup();
    context.pulse.mockRejectedValueOnce(new ApiError(429, 'rate_limited'));
    context.recorder.observe(reading());
    await settle();
    ticks(context.recorder, 1000, 10000);
    await settle();
    ticks(context.recorder, 11000, 69000);
    expect(context.pulse).toHaveBeenCalledTimes(1);
    now = 70000;
    context.recorder.observe(reading());
    await settle();
    expect(context.pulse).toHaveBeenCalledTimes(2);
    expect(context.pulse.mock.calls[1]?.[1]).toBe(context.pulse.mock.calls[0]?.[1]);
    context.recorder.stop();
  });
  it('stops after three failed attempts rather than keeping a queue or retry loop', async () => {
    const context = setup();
    context.pulse.mockRejectedValue(new ApiError(0, 'unavailable'));
    context.recorder.observe(reading());
    await settle();
    ticks(context.recorder, 1000, 10000);
    await settle();
    now = 20000;
    context.recorder.observe(reading());
    await settle();
    now = 30000;
    context.recorder.observe(reading());
    await settle();
    ticks(context.recorder, 40000, 60000);
    expect(context.pulse).toHaveBeenCalledTimes(3);
    expect(context.start).toHaveBeenCalledTimes(1);
  });
  it.each([400, 401, 403, 404, 409])('stops a visit after terminal HTTP %i', async (status) => {
    const context = setup();
    context.pulse.mockRejectedValue(new ApiError(status, 'blocked'));
    context.recorder.observe(reading());
    await settle();
    ticks(context.recorder, 1000, 10000);
    await settle();
    ticks(context.recorder, 20000, 40000);
    expect(context.pulse).toHaveBeenCalledTimes(1);
  });
  it('does not retry an invalid receipt or silently accept the wrong sequence', async () => {
    const context = setup();
    context.pulse.mockImplementationOnce(async (_id, packet) => ({
      ...receipt(packet),
      sequence: 99,
    }));
    context.recorder.observe(reading());
    await settle();
    ticks(context.recorder, 1000, 10000);
    await settle();
    ticks(context.recorder, 20000, 40000);
    expect(context.pulse).toHaveBeenCalledTimes(1);
  });
  it('does not accept credit greater than the submitted active time', async () => {
    const context = setup();
    context.pulse.mockImplementationOnce(async (_id, packet) => ({
      ...receipt(packet),
      creditedMs: packet.activeMs + 1,
    }));
    context.recorder.observe(reading());
    await settle();
    ticks(context.recorder, 1000, 10000);
    await settle();
    ticks(context.recorder, 20000, 40000);
    expect(context.pulse).toHaveBeenCalledTimes(1);
  });
  it('does not record an inactive reading viewport', async () => {
    const context = setup();
    context.recorder.observe({ ...reading(), visible: false });
    context.recorder.observe({ ...reading(), focused: false });
    context.recorder.observe({ ...reading(), articleTop: 1500 });
    await settle();
    expect(context.start).not.toHaveBeenCalled();
  });
  it('aborts on disposal and ignores an opening response that arrives later', async () => {
    const context = setup();
    let accept!: (value: ConsumptionSession) => void;
    context.start.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          accept = resolve;
        }),
    );
    context.recorder.observe(reading());
    const signal = context.start.mock.calls[0]?.[3];
    context.recorder.stop();
    expect(signal?.aborted).toBe(true);
    accept({
      sessionId,
      startedUtc: '2026-10-06T12:00:00Z',
      nextSequence: 1,
      sourceKind: 'reading',
    });
    await settle();
    ticks(context.recorder, 1000, 20000);
    expect(context.pulse).not.toHaveBeenCalled();
  });
  it('keeps the visitId through bounded opening retries', async () => {
    const context = setup();
    context.start.mockRejectedValueOnce(new ApiError(503, 'unavailable'));
    context.recorder.observe(reading());
    await settle();
    now = 10000;
    context.recorder.observe(reading());
    await settle();
    expect(context.start.mock.calls.map((call) => call[1])).toEqual([visitId, visitId]);
    context.recorder.stop();
  });
  it('rejects an opening response for the wrong source kind', async () => {
    const context = setup();
    context.start.mockResolvedValueOnce({
      sessionId,
      startedUtc: '2026-10-06T12:00:00Z',
      nextSequence: 1,
      sourceKind: 'youtube',
    });
    context.recorder.observe(reading());
    await settle();
    ticks(context.recorder, 1000, 20000);
    expect(context.pulse).not.toHaveBeenCalled();
  });
  it('does not infer elapsed time after a delayed browser tick', async () => {
    const context = setup();
    context.recorder.observe(reading());
    await settle();
    now = 30000;
    context.recorder.observe(reading());
    context.recorder.flush();
    await settle();
    expect(context.pulse).not.toHaveBeenCalled();
    context.recorder.stop();
  });
  it('opens a YouTube visit only on its first visible PLAYING observation', async () => {
    const context = setup('youtube');
    context.recorder.observe(video('unstarted'));
    context.recorder.observe(video('paused'));
    context.recorder.observe({ ...video(), visible: false });
    expect(context.start).not.toHaveBeenCalled();
    context.recorder.observe(video());
    await settle();
    expect(context.start).toHaveBeenCalledTimes(1);
    context.recorder.stop();
  });
  it('sends official sampled media positions without a reading payload', async () => {
    const context = setup('youtube');
    context.recorder.observe(video());
    await settle();
    for (now = 1000; now <= 10000; now += 1000) context.recorder.observe(video());
    await settle();
    expect(context.pulse.mock.calls[0]?.[1]).toEqual({
      sequence: 1,
      intervalMs: 10000,
      activeMs: 9000,
      media: {
        state: 'playing',
        positionMs: 10000,
        durationMs: 60000,
        playbackRateMilli: 1000,
        segments: [{ from: 1000, to: 10000 }],
      },
    });
    context.recorder.stop();
  });
});

describe('media metadata packet boundaries', () => {
  it('flushes already observed playback using its original rate before recording a new rate', async () => {
    const context = setup('youtube');
    context.recorder.observe(video());
    await settle();
    now = 1000;
    context.recorder.observe(video());
    now = 2000;
    context.recorder.observe(video());
    now = 3000;
    context.recorder.observe({ ...video(), playbackRateMilli: 2000, positionMs: 6000 });
    await settle();
    expect(context.pulse.mock.calls[0]?.[1]).toMatchObject({
      media: { playbackRateMilli: 1000, segments: [{ from: 1000, to: 2000 }] },
      activeMs: 1000,
    });
    for (now = 4000; now <= 13000; now += 1000)
      context.recorder.observe({ ...video(), playbackRateMilli: 2000, positionMs: now * 2 });
    await settle();
    expect(context.pulse.mock.calls[1]?.[1]).toMatchObject({
      media: { playbackRateMilli: 2000 },
      activeMs: 9000,
    });
    context.recorder.stop();
  });
  it('forwards changed duration separately without reusing old ranges or claiming an ended percentage', async () => {
    const context = setup('youtube');
    context.recorder.observe(video());
    await settle();
    now = 1000;
    context.recorder.observe(video());
    now = 2000;
    context.recorder.observe(video());
    now = 3000;
    context.recorder.observe({ ...video(), durationMs: 3000 });
    await settle();
    expect(context.pulse.mock.calls[0]?.[1]).toMatchObject({
      media: { durationMs: 60000, segments: [{ from: 1000, to: 2000 }] },
    });
    for (now = 4000; now <= 13000; now += 1000)
      context.recorder.observe({ ...video(), durationMs: 3000, positionMs: 3000 });
    await settle();
    expect(context.pulse.mock.calls[1]?.[1]).toMatchObject({
      media: { durationMs: 3000, segments: [], state: 'playing' },
      activeMs: 0,
    });
    context.recorder.stop();
  });
});
