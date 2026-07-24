import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { favoritesService } from '@/lib/api/services/favoritesService';
import { type FavoriteList } from '@/lib/api/types';

const FAVORITES_KEY = ['favorites'];

/** All favorites lists for the current user, with member series ids. */
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

export function useAddFavoriteItem() {
  return useInvalidatingMutation(({ listId, seriesId }: { listId: string; seriesId: string }) =>
    favoritesService.addItem(listId, seriesId));
}

export function useRemoveFavoriteItem() {
  return useInvalidatingMutation(({ listId, seriesId }: { listId: string; seriesId: string }) =>
    favoritesService.removeItem(listId, seriesId));
}
