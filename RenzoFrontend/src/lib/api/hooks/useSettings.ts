import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { settingsService } from '@/lib/api/services/settingsService';
import { setPublicApiUrl } from '@/lib/api/config';
import { type Settings } from '@/lib/api/types';

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
