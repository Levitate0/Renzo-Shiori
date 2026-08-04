import { getApiConfig } from './config';

// Single in-flight silent-refresh shared across concurrent 401s, so a burst of
// failing requests triggers exactly one /api/auth/refresh (the endpoint rotates
// the refresh token, so parallel calls would invalidate each other).
let refreshPromise: Promise<boolean> | null = null;

async function trySilentRefresh(baseUrl: string): Promise<boolean> {
  refreshPromise ??= (async () => {
    try {
      const response = await fetch(`${baseUrl}/api/auth/refresh`, {
        method: 'POST',
        credentials: 'include',
        cache: 'no-store',
      });
      if (!response.ok) return false;
      const body = (await response.json()) as { token?: string };
      if (!body?.token) return false;
      sessionStorage.setItem('renzo_token', body.token);
      return true;
    } catch {
      return false;
    } finally {
      // Allow the next expiry (hours later) to refresh again.
      setTimeout(() => { refreshPromise = null; }, 0);
    }
  })();
  return refreshPromise;
}

/**
 * Session death is announced once, app-wide. Without this a token that expires
 * (or a refresh cookie that's been revoked) leaves every request failing 401
 * while the UI keeps rendering: a blank library, empty settings, endless
 * spinners — with no hint that the fix is to sign in again. The auth context
 * listens for this and clears the user, which its existing redirect effect
 * turns into a trip back to /login (or /user-select in profile mode).
 */
export const SESSION_EXPIRED_EVENT = 'renzo:session-expired';

let sessionExpiredAnnounced = false;

function announceSessionExpired(): void {
  if (typeof window === 'undefined' || sessionExpiredAnnounced) return;
  sessionExpiredAnnounced = true;
  try { sessionStorage.removeItem('renzo_token'); } catch { /* private mode */ }
  window.dispatchEvent(new Event(SESSION_EXPIRED_EVENT));
  // A fresh sign-in must be able to expire again later.
  setTimeout(() => { sessionExpiredAnnounced = false; }, 5000);
}

class RenzoApiClient {
  // Resolved per-request (not captured at construction) so the WebUI-configured
  // public URL takes effect as soon as settings load, without a page reload.
  private get baseUrl(): string {
    return getApiConfig().baseUrl;
  }

  private getAuthToken(): string | null {
    if (typeof window === 'undefined') return null;
    return sessionStorage.getItem('renzo_token');
  }

  private getSelectedUser(): string | null {
    if (typeof window === 'undefined') return null;
    return localStorage.getItem('renzo_selected_user');
  }

  private async request<T>(
    endpoint: string,
    options: RequestInit = {},
    isRetryAfterRefresh = false
  ): Promise<T> {
    const url = this.baseUrl ? `${this.baseUrl}${endpoint}` : endpoint;

    // Determine if we're sending FormData
    const isFormData = options.body instanceof FormData;

    // Build headers
    const headers: Record<string, string> = {
      // Only set Content-Type for non-FormData requests
      // FormData requests need the browser to set Content-Type with boundary
      ...(isFormData ? {} : { 'Content-Type': 'application/json' }),
      ...(options.headers as Record<string, string> || {}),
    };

    // Attach Bearer token if available (JWT-based auth)
    const token = this.getAuthToken();
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    } else {
      // Fallback to header-based user selection for non-auth mode
      const username = this.getSelectedUser();
      if (username) {
        headers['X-Renzo-User'] = username;
      }
    }

    const response = await fetch(url, {
      headers,
      credentials: 'include', // Include cookies for session management
      // Always bypass the browser HTTP cache for API calls. Without this, a GET
      // like /api/auth/status can be served from a stale cached copy (e.g. one
      // captured while the DB was empty: authenticationEnabled=false), which wedges
      // the whole auth flow even though the server now returns the correct value.
      cache: 'no-store',
      ...options,
    });

    // Access token expired mid-session: silently refresh via the httpOnly cookie
    // and retry once. Without this, a token expiring while the app is open leaves
    // every call failing 401 until a manual page reload. Auth endpoints are
    // excluded — a 401 there is a genuine credential failure, not expiry.
    if (
      response.status === 401 &&
      !isRetryAfterRefresh &&
      token !== null &&
      !endpoint.startsWith('/api/auth/')
    ) {
      const refreshed = await trySilentRefresh(this.baseUrl);
      if (refreshed) {
        return this.request<T>(endpoint, options, true);
      }
      // Refresh failed: the session is genuinely over (expired refresh token,
      // revoked session, server restarted with new keys). Say so once instead
      // of letting every screen fail silently.
      announceSessionExpired();
    } else if (response.status === 401 && !endpoint.startsWith('/api/auth/')) {
      // 401 with no token at all — same conclusion, no refresh to attempt.
      announceSessionExpired();
    }

    if (!response.ok) {
      // Try to extract a meaningful error message from the response body
      let errorMessage = `API Error: ${response.status} ${response.statusText}`;
      try {
        const errorText = await response.text();
        if (errorText?.trim()) {
          const errorBody = JSON.parse(errorText) as Record<string, unknown>;
          if (typeof errorBody?.error === 'string') {
            errorMessage = errorBody.error;
          } else if (typeof errorBody?.message === 'string') {
            errorMessage = errorBody.message;
          } else if (typeof errorBody?.title === 'string') {
            errorMessage = errorBody.title;
          }
        }
      } catch {
        // Ignore parsing errors, use the default error message
      }
      throw new Error(errorMessage);
    }    // Handle empty responses properly
    const contentLength = response.headers.get('content-length');
    const contentType = response.headers.get('content-type');
    
    // Check for explicitly empty responses
    if (contentLength === '0' || response.status === 204) {
      return undefined as T;
    }

    // For responses that should contain JSON, check if content-type indicates JSON
    const isJsonResponse = contentType?.includes('application/json');
    
    try {
      // Try to get response text first
      const text = await response.text();
      
      // If no text content or only whitespace, return undefined
      if (!text || text.trim() === '') {
        return undefined as T;
      }      // If response has content, try to parse as JSON
      if (isJsonResponse ?? (text.trim().startsWith('{') ?? text.trim().startsWith('['))) {
        try {
          const result = JSON.parse(text) as { data?: T } | T;
          return result && typeof result === 'object' && 'data' in result && result.data !== undefined 
            ? result.data 
            : result as T;
        } catch (jsonError) {
          // JSON parsing failed, but we have text content
          // For void API endpoints, this might still be valid if text is minimal
          if (text.trim() === '{}' || text.trim() === 'null') {
            return undefined as T;
          }
          throw new Error(`Invalid JSON response: ${text}`);
        }
      }
      
      // Non-JSON response with content - return as string cast to T
      return text as T;
      
    } catch (error) {
      // Handle fetch/text reading errors
      if (error instanceof Error && error.message.includes('JSON')) {
        throw error;
      }
      
      // For other errors, assume it's a void response if status is 200
      if (response.status === 200) {
        return undefined as T;
      }
      
      throw new Error(`Failed to process response: ${error instanceof Error ? error.message : 'Unknown error'}`);
    }
  }

  async get<T>(endpoint: string): Promise<T> {
    return this.request<T>(endpoint, { method: 'GET' });
  }
  async post<T>(endpoint: string, data?: unknown, _options?: { params?: Record<string, unknown> }): Promise<T> {
    return this.request<T>(endpoint, {
      method: 'POST',
      body: data instanceof FormData ? data : (data ? JSON.stringify(data) : undefined),
    });
  }
  async put<T>(endpoint: string, data?: unknown): Promise<T> {
    return this.request<T>(endpoint, {
      method: 'PUT',
      body: data ? JSON.stringify(data) : undefined,
    });
  }

  async patch<T>(endpoint: string, data?: unknown): Promise<T> {
    return this.request<T>(endpoint, {
      method: 'PATCH',
      body: data ? JSON.stringify(data) : undefined,
    });
  }

  async delete<T>(endpoint: string): Promise<T> {
    return this.request<T>(endpoint, { method: 'DELETE' });
  }
}

export const apiClient = new RenzoApiClient();
