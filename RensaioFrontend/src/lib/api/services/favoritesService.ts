import { apiClient } from '@/lib/api/client';
import { type FavoriteList } from '@/lib/api/types';

export const favoritesService = {
  /** All of the current user's lists with their member series ids. */
  async getAll(): Promise<FavoriteList[]> {
    return apiClient.get<FavoriteList[]>('/api/favorites');
  },

  /** Create a top-level tab, or a sub-list when parentId is given. */
  async createList(name: string, parentId?: string): Promise<FavoriteList> {
    return apiClient.post<FavoriteList>('/api/favorites', { name, parentId });
  },

  async renameList(id: string, name: string): Promise<void> {
    return apiClient.put<void>(`/api/favorites/${id}`, { name });
  },

  /** Deletes a list, its sub-lists, and all memberships. */
  async deleteList(id: string): Promise<void> {
    return apiClient.delete<void>(`/api/favorites/${id}`);
  },

  async addItem(listId: string, seriesId: string): Promise<void> {
    return apiClient.post<void>(`/api/favorites/${listId}/items`, { seriesId });
  },

  async removeItem(listId: string, seriesId: string): Promise<void> {
    return apiClient.delete<void>(`/api/favorites/${listId}/items/${seriesId}`);
  },
};
