import { getApiConfig } from "@/lib/api/config";
import { getCachedImageToken } from "@/lib/api/imageToken";

/**
 * Cover placeholder for series with no thumbnail — the Renzō mark, matched
 * to the active light/dark theme. Reads the `.dark` class next-themes puts
 * on <html> directly rather than useTheme(), since this is a plain function
 * called from JSX `src={...}` all over the app, not a component that could
 * subscribe to theme context. Defaults to the dark variant during SSR/before
 * hydration, matching the app's dark default (see layout.tsx's no-flash
 * script and defaultTheme="system" — most users land on dark).
 */
function missingCoverPlaceholder(): string {
  if (typeof document !== "undefined" && !document.documentElement.classList.contains("dark")) {
    return "/renzo-icon-light.png";
  }
  return "/renzo-icon-dark.png";
}

/**
 * Returns a fully qualified URL for a thumbnail/image path.
 *
 * - When `baseUrl` is set (e.g., dev mode separated from backend), the backend
 *   origin is prepended so the browser fetches from the correct port.
 * - When `thumbnailUrl` is falsy or empty, returns a local placeholder so the
 *   frontend never makes a needless network round-trip to the backend.
 * - When `thumbnailUrl` already starts with `http`, returns it as-is.
 * - When JWT authentication is enabled, appends a short-lived, narrowly-scoped
 *   image token as a `?token=` query parameter (see imageToken.ts). <img
 *   src="..."> tags are loaded natively by the browser and cannot attach an
 *   Authorization header the way apiClient's fetch() calls do, so without
 *   this the backend's AuthMiddleware rejects every image request with 401
 *   regardless of having a valid, logged-in session. This intentionally does
 *   NOT use the main session token (sessionStorage 'renzo_token') - that
 *   token is long-lived and full-scope, and putting it in a URL would leak it
 *   into browser history, server access logs, and any intermediate proxy/CDN
 *   logs. The image token is short-lived (15 min) and can only ever be used
 *   for image requests, so exposure there is low-value even if it leaks.
 */
export const formatThumbnailUrl = (thumbnailUrl?: string): string => {
  const config = getApiConfig();
  if (!thumbnailUrl) {
    return missingCoverPlaceholder();
  }
  if (thumbnailUrl.startsWith("http")) {
    return thumbnailUrl;
  }
  const url = `${config.baseUrl}${thumbnailUrl}`;
  if (typeof window === "undefined") {
    return url;
  }
  const token = getCachedImageToken();
  if (!token) {
    return url;
  }
  const separator = url.includes("?") ? "&" : "?";
  return `${url}${separator}token=${encodeURIComponent(token)}`;
};
