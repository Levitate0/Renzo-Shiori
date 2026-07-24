import { apiClient } from './client';

const STORAGE_TOKEN_KEY = 'renzo_image_token';
const STORAGE_EXPIRY_KEY = 'renzo_image_token_expiry';
// Refresh a bit before actual expiry so image loads already in flight aren't
// cut off mid-request by the backend rejecting a token that expired a second
// after the URL was built.
const REFRESH_SKEW_MS = 60 * 1000;

interface ImageTokenResponse {
  token: string;
  expiresAt: string;
}

let refreshPromise: Promise<string | null> | null = null;

function readCachedToken(): { token: string; expiresAt: number } | null {
  if (typeof window === 'undefined') return null;
  const token = sessionStorage.getItem(STORAGE_TOKEN_KEY);
  const expiryRaw = sessionStorage.getItem(STORAGE_EXPIRY_KEY);
  if (!token || !expiryRaw) return null;
  const expiresAt = parseInt(expiryRaw, 10);
  if (Number.isNaN(expiresAt)) return null;
  return { token, expiresAt };
}

function storeToken(token: string, expiresAtIso: string) {
  if (typeof window === 'undefined') return;
  const expiresAtMs = new Date(expiresAtIso).getTime();
  sessionStorage.setItem(STORAGE_TOKEN_KEY, token);
  sessionStorage.setItem(STORAGE_EXPIRY_KEY, String(expiresAtMs));
}

/**
 * Fetches a fresh short-lived image-access token from the backend
 * (GET /api/auth/image-token) if the cached one is missing or close to
 * expiring. Safe to call repeatedly - concurrent callers share a single
 * in-flight request rather than each firing their own.
 */
export async function ensureImageToken(): Promise<string | null> {
  const cached = readCachedToken();
  if (cached && Date.now() < cached.expiresAt - REFRESH_SKEW_MS) {
    return cached.token;
  }

  if (!refreshPromise) {
    refreshPromise = apiClient
      .get<ImageTokenResponse>('/api/auth/image-token')
      .then((res) => {
        storeToken(res.token, res.expiresAt);
        return res.token;
      })
      .catch(() => null)
      .finally(() => {
        refreshPromise = null;
      });
  }

  return refreshPromise;
}

/**
 * Returns a cached image-scoped token synchronously, for use by
 * formatThumbnailUrl(), which is called during render for every <img src>
 * and can't await an async fetch. If there's no cached token yet, or it's
 * close to expiring, a background refresh is kicked off via ensureImageToken()
 * so the *next* render picks up a fresh one - the current call still returns
 * whatever it has (or null) rather than blocking.
 */
export function getCachedImageToken(): string | null {
  const cached = readCachedToken();
  if (!cached) {
    void ensureImageToken();
    return null;
  }
  if (Date.now() >= cached.expiresAt - REFRESH_SKEW_MS) {
    void ensureImageToken();
    return cached.token;
  }
  return cached.token;
}

/**
 * Clears the cached image token. Call this on logout so a stale token isn't
 * left sitting in sessionStorage for the next user on a shared machine.
 */
export function clearImageToken(): void {
  if (typeof window === 'undefined') return;
  sessionStorage.removeItem(STORAGE_TOKEN_KEY);
  sessionStorage.removeItem(STORAGE_EXPIRY_KEY);
}
