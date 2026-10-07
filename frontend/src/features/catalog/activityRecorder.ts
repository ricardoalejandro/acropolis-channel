import { ApiError } from '../../api/identity';
import {
  consumptionActivity,
  type ConsumptionPulse,
  type ConsumptionReceipt,
  type ConsumptionSession,
  type ConsumptionSource,
} from '../../api/consumptionActivity';
import {
  mergeObservedRanges,
  readingStep,
  youtubeStep,
  type ObservedRange,
  type ReadingSample,
  type YouTubeSample,
} from './activitySampling';
export type ActivitySample = ReadingSample | YouTubeSample;
export type ActivityTransport = {
  start: typeof consumptionActivity.start;
  pulse: typeof consumptionActivity.pulse;
};
const PERIOD_MS = 10000;
const MAX_ATTEMPTS = 3;
function retryable(error: unknown) {
  return (
    error instanceof ApiError &&
    (error.status === 429 ||
      error.status === 503 ||
      (error.status === 0 && error.code !== 'invalid_response'))
  );
}
export class ActivityRecorder {
  private session: ConsumptionSession | null = null;
  private opening = false;
  private stopped = false;
  private controller = new AbortController();
  private previous: ActivitySample | null = null;
  private latest: ActivitySample | null = null;
  private began = 0;
  private activeMs = 0;
  private ranges: ObservedRange[] = [];
  private position = 0;
  private stateDirty = false;
  private pending: ConsumptionPulse | null = null;
  private inFlight = false;
  private attempts = 0;
  private retryAt = 0;
  constructor(
    private readonly slug: string,
    private readonly version: string,
    private readonly visitId: string,
    private readonly source: ConsumptionSource,
    private readonly transport: ActivityTransport = consumptionActivity,
    private readonly clock = () => performance.now(),
  ) {}
  private reset(now: number) {
    this.previous = null;
    this.began = now;
    this.activeMs = 0;
    this.ranges = [];
    this.position = 0;
  }
  private async open() {
    if (this.opening || this.stopped || this.session || this.clock() < this.retryAt) return;
    this.opening = true;
    try {
      const session = await this.transport.start(
        this.slug,
        this.visitId,
        this.version,
        this.controller.signal,
      );
      if (this.stopped) return;
      if (session.sourceKind !== this.source) {
        this.stop();
        return;
      }
      this.session = session;
      this.attempts = 0;
      this.retryAt = 0;
      this.reset(this.clock());
    } catch (error) {
      if (!this.stopped && retryable(error) && ++this.attempts < MAX_ATTEMPTS)
        this.retryAt =
          this.clock() + (error instanceof ApiError && error.status === 429 ? 60000 : PERIOD_MS);
      else this.stop();
    } finally {
      this.opening = false;
    }
  }
  observe(sample: ActivitySample, transition = false) {
    if (this.stopped) return;
    if (this.source === 'youtube' && this.latest) {
      const previous = this.latest as YouTubeSample;
      const current = sample as YouTubeSample;
      const durationChanged =
        previous.durationMs !== current.durationMs &&
        (previous.durationMs === null ||
          current.durationMs === null ||
          Math.abs(previous.durationMs - current.durationMs) > 1000);
      if (previous.playbackRateMilli !== current.playbackRateMilli || durationChanged) {
        this.flush();
        transition = true;
      }
    }
    this.latest = sample;
    if (transition) {
      this.stateDirty = true;
      this.previous = null;
    }
    if (!this.session) {
      const eligible =
        this.source === 'reading'
          ? readingStep(null, sample as ReadingSample).exposedRanges.length > 0
          : (sample as YouTubeSample).state === 'playing' && sample.visible && sample.focused;
      if (eligible) void this.open();
      return;
    }
    if (this.inFlight || this.pending) {
      this.previous = null;
      if (this.pending && !this.inFlight && this.clock() >= this.retryAt) void this.send();
      return;
    }
    if (sample.now < this.began || sample.now - this.began > 15000) this.reset(sample.now);
    if (this.source === 'reading') {
      const observed = readingStep(this.previous as ReadingSample | null, sample as ReadingSample);
      this.activeMs += observed.activeMs;
      this.ranges = mergeObservedRanges([...this.ranges, ...observed.exposedRanges]);
      this.position = Math.max(this.position, observed.positionBasisPoints);
    } else {
      const observed = youtubeStep(this.previous as YouTubeSample | null, sample as YouTubeSample);
      this.activeMs += observed.activeMs;
      this.ranges = mergeObservedRanges([...this.ranges, ...observed.segments]);
      if (transition && (sample as YouTubeSample).state === 'ended') this.stateDirty = true;
    }
    this.previous = sample;
    if (sample.now - this.began >= PERIOD_MS) this.flush();
  }
  flush() {
    if (this.stopped || !this.session || !this.latest || this.pending || this.inFlight) return;
    const intervalMs = Math.floor(this.latest.now - this.began);
    if (intervalMs < 1 || intervalMs > 15000) {
      this.reset(this.clock());
      return;
    }
    const activeMs = Math.min(intervalMs, Math.floor(this.activeMs));
    const ranges = this.ranges.slice(0, 16);
    if (this.source === 'reading') {
      if (!ranges.length && activeMs === 0) {
        this.reset(this.clock());
        return;
      }
      this.pending = {
        sequence: this.session.nextSequence,
        intervalMs,
        activeMs,
        reading: { positionBasisPoints: this.position, exposedRanges: ranges },
      };
    } else {
      const sample = this.latest as YouTubeSample;
      if (sample.state === 'unstarted' || (!ranges.length && activeMs === 0 && !this.stateDirty)) {
        this.reset(this.clock());
        return;
      }
      this.pending = {
        sequence: this.session.nextSequence,
        intervalMs,
        activeMs,
        media: {
          state: sample.state,
          positionMs: sample.positionMs,
          durationMs: sample.durationMs,
          playbackRateMilli: sample.playbackRateMilli,
          segments: ranges,
        },
      };
    }
    this.stateDirty = false;
    this.attempts = 0;
    this.retryAt = 0;
    this.reset(this.clock());
    void this.send();
  }
  private async send() {
    if (this.stopped || !this.session || !this.pending || this.inFlight) return;
    this.inFlight = true;
    const packet = this.pending;
    try {
      const receipt: ConsumptionReceipt = await this.transport.pulse(
        this.session.sessionId,
        packet,
        this.controller.signal,
      );
      if (this.stopped) return;
      if (receipt.sequence !== packet.sequence || receipt.creditedMs > packet.activeMs) {
        this.stop();
        return;
      }
      this.session = { ...this.session, nextSequence: receipt.sequence + 1 };
      this.pending = null;
      this.retryAt = 0;
      this.attempts = 0;
      this.reset(this.clock());
    } catch (error) {
      if (!this.stopped && retryable(error) && ++this.attempts < MAX_ATTEMPTS)
        this.retryAt =
          this.clock() + (error instanceof ApiError && error.status === 429 ? 60000 : PERIOD_MS);
      else this.stop();
    } finally {
      this.inFlight = false;
    }
  }
  transition(sample: ActivitySample) {
    // Flush the last observed state first; a pause must not relabel already sampled playback.
    this.flush();
    this.observe(sample, true);
  }
  stop() {
    this.stopped = true;
    this.controller.abort();
    this.pending = null;
    this.previous = null;
    this.ranges = [];
  }
}
