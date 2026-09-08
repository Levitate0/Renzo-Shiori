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
