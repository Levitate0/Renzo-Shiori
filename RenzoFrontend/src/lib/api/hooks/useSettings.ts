import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { settingsService } from '@/lib/api/services/settingsService';
import { setPublicApiUrl } from '@/lib/api/config';
import { type Settings, type ContentPreferences } from '@/lib/api/types';

export const useSettings = () => {
  return useQuery({
    queryKey: ['settings'],
    queryFn: async () => {
      const settings = await settingsService.getSettings();
      // Keep the API client's public-URL override in sync with the WebUI setting.
      setPublicApiUrl(settings.externalDomain);
      return settings;
    },
  });
};

export const useAvailableLanguages = () => {
  return useQuery({
    queryKey: ['settings', 'languages'],
    queryFn: () => settingsService.getAvailableLanguages(),
  });
};

export const useUpdateSettings = () => {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (settings: Settings) => settingsService.updateSettings(settings),
    onSuccess: (data) => {
      // If the backend returned a set-password URL, the settings-manager
      // will handle the redirect. Otherwise, invalidate settings.
      if (!data?.setPasswordUrl) {
        queryClient.invalidateQueries({ queryKey: ['settings'] });
      }
    },
  });
};

/**
 * Reorders the categories from the series page's Category picker. The new
 * order shows at once; a failure puts the saved one back.
 */
export const useReorderCategories = () => {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (order: string[]) => settingsService.reorderCategories(order),
    onMutate: async (order) => {
      await queryClient.cancelQueries({ queryKey: ['settings'], exact: true });
      const previous = queryClient.getQueryData<Settings>(['settings']);
      if (previous) queryClient.setQueryData<Settings>(['settings'], { ...previous, categories: order });
      return { previous };
    },
    onError: (_err, _order, context) => {
      if (context?.previous) queryClient.setQueryData(['settings'], context.previous);
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['settings'], exact: true });
    },
  });
};

/** The calling user's own content preferences (language order, 18+, download-all). */
export const useContentPreferences = () => {
  return useQuery({
    queryKey: ['settings', 'content-preferences'],
    queryFn: () => settingsService.getContentPreferences(),
  });
};

export const useUpdateContentPreferences = () => {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (prefs: ContentPreferences) => settingsService.updateContentPreferences(prefs),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['settings', 'content-preferences'] });
      // Library/Browse filter by language and 18+, so their cached pages are
      // stale the moment these change.
      queryClient.invalidateQueries({ queryKey: ['series'] });
    },
  });
};
