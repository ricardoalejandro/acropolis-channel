import { act, renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { useCatalog } from './useCatalog';
describe('Catalogue request ordering', () => {
  for (const late of ['success', 'failure'])
    it('does not replace a newer route with a late old ' + late, async () => {
      let release: ((value: number) => void) | undefined;
      let reject: ((error: Error) => void) | undefined;
      const old = () =>
        new Promise<number>((resolve, fail) => {
          release = resolve;
          reject = fail;
        });
      const next = vi.fn().mockResolvedValue(2);
      const { result, rerender } = renderHook(({ load, key }) => useCatalog(load, key), {
        initialProps: { load: old, key: 'old' },
      });
      rerender({ load: next, key: 'new' });
      await waitFor(() => expect(result.current.data).toBe(2));
      await act(async () => {
        if (late === 'success') release?.(1);
        else reject?.(new Error('private failure from stale request'));
      });
      expect(result.current.data).toBe(2);
      expect(result.current.error).toBe('');
    });
  it('hides data from the previous key while a new response is pending and cancels abandoned views', async () => {
    const first = () => Promise.resolve(1);
    let release: ((value: number) => void) | undefined;
    const next = () =>
      new Promise<number>((resolve) => {
        release = resolve;
      });
    const { result, rerender, unmount } = renderHook(({ load, key }) => useCatalog(load, key), {
      initialProps: { load: first, key: 'one' },
    });
    await waitFor(() => expect(result.current.data).toBe(1));
    rerender({ load: next, key: 'two' });
    expect(result.current.data).toBeNull();
    expect(result.current.loading).toBe(true);
    unmount();
    await act(async () => {
      release?.(2);
    });
  });
});
