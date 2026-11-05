import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { favoritesService } from '@/lib/api/services/favoritesService';
import { type FavoriteList } from '@/lib/api/types';

const FAVORITES_KEY = ['favorites'];
const REORDER_KEY = ['favorites', 'reorder'];

/** All of the current user's collections, with member series ids. */
export function useFavorites() {
  return useQuery<FavoriteList[]>({
    queryKey: FAVORITES_KEY,
    queryFn: () => favoritesService.getAll(),
    staleTime: 30 * 1000,
  });
}

function useInvalidatingMutation<TArgs>(fn: (args: TArgs) => Promise<unknown>) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: FAVORITES_KEY });
    },
  });
}

export function useCreateFavoriteList() {
  return useInvalidatingMutation(({ name, parentId }: { name: string; parentId?: string }) =>
    favoritesService.createList(name, parentId));
}

export function useRenameFavoriteList() {
  return useInvalidatingMutation(({ id, name }: { id: string; name: string }) =>
    favoritesService.renameList(id, name));
}

export function useDeleteFavoriteList() {
  return useInvalidatingMutation((id: string) => favoritesService.deleteList(id));
}

/**
 * Reorders one sibling set (see favoritesService.reorderLists). Optimistic: the
 * cached sortOrders change at once, so a row moves the moment its arrow is
 * pressed, and the snapshot is put back if the server refuses.
 *
 * Moves are queued (one scope) so their PUTs reach the server in click order —
 * two in flight at once could land swapped and leave the earlier order behind.
 * Each move is computed from the optimistic cache, so a queued one already
 * includes the moves before it. The refetch waits for the last queued move:
 * one that ran between moves would bring back a server order that predates
 * the moves still waiting, and the rows would jump back and forth.
 */
export function useReorderFavoriteLists() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationKey: REORDER_KEY,
    scope: { id: 'favorites-reorder' },
    mutationFn: ({ parentId, ids }: { parentId: string | null; ids: string[] }) =>
      favoritesService.reorderLists(parentId, ids),
    onMutate: async ({ ids }) => {
      // A refetch already in flight would land on top of the optimistic order.
      await queryClient.cancelQueries({ queryKey: FAVORITES_KEY });
      const previous = queryClient.getQueryData<FavoriteList[]>(FAVORITES_KEY);
      const position = new Map(ids.map((id, index) => [id, index]));
      queryClient.setQueryData<FavoriteList[]>(FAVORITES_KEY, (lists) =>
        lists?.map((l) => {
          const index = position.get(l.id);
          return index === undefined ? l : { ...l, sortOrder: index };
        }));
      return { previous };
    },
    onError: (_err, _vars, context) => {
      if (context?.previous) queryClient.setQueryData(FAVORITES_KEY, context.previous);
    },
    onSettled: () => {
      // This move still counts as pending here, so 1 means none are queued behind it.
      if (queryClient.isMutating({ mutationKey: REORDER_KEY }) === 1) {
        void queryClient.invalidateQueries({ queryKey: FAVORITES_KEY });
      }
    },
  });
}

export function useAddFavoriteItem() {
  return useInvalidatingMutation(({ listId, seriesId }: { listId: string; seriesId: string }) =>
    favoritesService.addItem(listId, seriesId));
}

export function useRemoveFavoriteItem() {
  return useInvalidatingMutation(({ listId, seriesId }: { listId: string; seriesId: string }) =>
    favoritesService.removeItem(listId, seriesId));
}
