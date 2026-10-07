import { useCallback } from 'react';
import { subscriptions } from '../../api/subscriptions';
import { useCatalog } from '../catalog/useCatalog';
export function useSubscriptionAccounts(userIds: string[]) {
  const key = [...new Set(userIds)].sort().join(',');
  const load = useCallback(() => subscriptions.lookupAccounts(key ? key.split(',') : []), [key]);
  return useCatalog(load, key);
}
