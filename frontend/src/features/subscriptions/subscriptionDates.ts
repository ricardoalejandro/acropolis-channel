import type { SubscriptionPlan } from '../../api/subscriptions';
const limaOffset = 5 * 60 * 60 * 1000;
export function limaStart(value: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?$/.test(value) || value.startsWith('0000-'))
    return null;
  const normalized = value.length === 16 ? value + ':00' : value;
  const instant = Date.parse(normalized + '-05:00');
  if (!Number.isFinite(instant)) return null;
  const local = new Date(instant - limaOffset).toISOString().slice(0, 19);
  return local === normalized ? new Date(instant).toISOString() : null;
}
export function limaInput(value: string): string {
  return new Date(Date.parse(value) - limaOffset).toISOString().slice(0, 19);
}
export function planPeriodEnd(plan: SubscriptionPlan, startsUtc: string): string | null {
  if (plan === 'free_beta') return null;
  const start = new Date(startsUtc);
  if (!Number.isFinite(start.getTime())) return null;
  const month = start.getUTCMonth() + (plan === 'probationismo' ? 3 : 12);
  const year = start.getUTCFullYear() + Math.floor(month / 12);
  if (year > 9999) return null;
  const targetMonth = month % 12;
  const lastDay = new Date(Date.UTC(year, targetMonth + 1, 0)).getUTCDate();
  start.setUTCFullYear(year, targetMonth, Math.min(start.getUTCDate(), lastDay));
  return start.toISOString();
}
