import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { statusService } from '@/lib/api/services/statusService';
import type { SeriesHealth, ProviderHealth, StatusSummary, ClearAlertRequest } from '@/lib/api/types';

export const useSeriesStatus = (viewAll = false) => {
  return useQuery<SeriesHealth[]>({
    queryKey: ['status', 'series', viewAll],
    queryFn: () => statusService.getSeriesStatus(viewAll),
    staleTime: 30 * 1000, // 30 seconds
    refetchOnWindowFocus: true,
  });
};

export const useProviderStatus = (viewAll = false) => {
  return useQuery<ProviderHealth[]>({
    queryKey: ['status', 'providers', viewAll],
    queryFn: () => statusService.getProviderStatus(viewAll),
    staleTime: 30 * 1000,
    refetchOnWindowFocus: true,
  });
};

export const useStatusSummary = (viewAll = false) => {
  return useQuery<StatusSummary>({
    queryKey: ['status', 'summary', viewAll],
    queryFn: () => statusService.getStatusSummary(viewAll),
    staleTime: 30 * 1000,
    refetchOnWindowFocus: true,
  });
};

export const useClearAlert = () => {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (request: ClearAlertRequest) => statusService.clearAlert(request),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['status'] });
    },
  });
};