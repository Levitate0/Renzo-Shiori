import { apiClient } from '@/lib/api/client';
import { type FavoriteList } from '@/lib/api/types';

export const favoritesService = {
  /** All of the current user's collections with their member series ids. */
  async getAll(): Promise<FavoriteList[]> {
    return apiClient.get<FavoriteList[]>('/api/favorites');
  },

  /** Create a top-level collection, or a sub-collection when parentId is given. */
  async createList(name: string, parentId?: string): Promise<FavoriteList> {
    return apiClient.post<FavoriteList>('/api/favorites', { name, parentId });
  },

  async renameList(id: string, name: string): Promise<void> {
    return apiClient.put<void>(`/api/favorites/${id}`, { name });
  },

  /** Deletes a collection, its sub-collections, and all memberships. */
  async deleteList(id: string): Promise<void> {
    return apiClient.delete<void>(`/api/favorites/${id}`);
  },

  /**
   * Sets the order of one sibling set: the top-level collections when parentId
   * is null, otherwise that parent's sub-collections. `ids` must be the WHOLE
   * set in its new order — the server refuses (409) any set that isn't
   * exactly its own, so a dialog open on stale data can't write half an order.
   */
  async reorderLists(parentId: string | null, ids: string[]): Promise<void> {
    return apiClient.put<void>('/api/favorites/order', { parentId, ids });
  },

  async addItem(listId: string, seriesId: string): Promise<void> {
    return apiClient.post<void>(`/api/favorites/${listId}/items`, { seriesId });
  },

  async removeItem(listId: string, seriesId: string): Promise<void> {
    return apiClient.delete<void>(`/api/favorites/${listId}/items/${seriesId}`);
  },
};
