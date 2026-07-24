/**
 * Configuration utilities for API and SignalR connections
 */

export interface ApiConfig {
  baseUrl: string;
  isAbsolute: boolean;
}

/**
 * The server-configured public URL (Settings → External Domain in the WebUI),
 * persisted client-side whenever settings are fetched so the API client can
 * target it without a chicken-and-egg settings fetch on cold start.
 */
const PUBLIC_URL_STORAGE_KEY = 'renzo_public_url';

export function setPublicApiUrl(url: string | null | undefined): void {
  if (typeof window === 'undefined') return;
  const trimmed = url?.trim().replace(/\/+$/, '') ?? '';
  if (trimmed) {
    localStorage.setItem(PUBLIC_URL_STORAGE_KEY, trimmed);
  } else {
    localStorage.removeItem(PUBLIC_URL_STORAGE_KEY);
  }
}

/**
 * Determines the appropriate base URL for API and SignalR connections
 *
 * Logic:
 * - In development: always use http://127.0.0.1:9833
 * - In production: use the WebUI-configured public URL (External Domain setting)
 *   when set — unless the page is already being served from that origin, where
 *   relative paths are equivalent and avoid needless absolute-URL handling.
 *   Falls back to relative paths when the setting is empty/not yet known.
 *
 * @returns Configuration object with baseUrl and isAbsolute flag
 */
export function getApiConfig(): ApiConfig {
  // Only run in browser environment
  if (typeof window === 'undefined') {
    return {
      baseUrl: '',
      isAbsolute: false
    };
  }

  if (process.env.NODE_ENV === 'development') {
    return {
      baseUrl: 'http://127.0.0.1:9833',
      isAbsolute: true
    };
  }

  const publicUrl = localStorage.getItem(PUBLIC_URL_STORAGE_KEY);
  if (publicUrl) {
    try {
      if (new URL(publicUrl).origin !== window.location.origin) {
        return { baseUrl: publicUrl, isAbsolute: true };
      }
    } catch {
      // Malformed stored URL — ignore and use relative paths
    }
  }

  return {
    baseUrl: '',
    isAbsolute: false
  };
}

/**
 * Builds a complete URL for API endpoints
 * @param endpoint - The API endpoint (e.g., '/api/series')
 * @returns Complete URL
 */
export function buildApiUrl(endpoint: string): string {
  const config = getApiConfig();
  return config.baseUrl ? `${config.baseUrl}${endpoint}` : endpoint;
}

/**
 * Builds a complete URL for SignalR hub connections
 * @param hubPath - The hub path (e.g., '/progress')
 * @returns Complete URL
 */
export function buildSignalRUrl(hubPath: string): string {
  const config = getApiConfig();
  return config.baseUrl ? `${config.baseUrl}${hubPath}` : hubPath;
}
