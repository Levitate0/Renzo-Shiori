import { getApiConfig } from "@/lib/api/config";

/**
 * Returns a fully qualified URL for a thumbnail/image path.
 *
 * - When `baseUrl` is set (e.g., dev mode separated from backend), the backend
 *   origin is prepended so the browser fetches from the correct port.
 * - When `thumbnailUrl` is falsy or empty, returns a local placeholder so the
 *   frontend never makes a needless network round-trip to the backend.
 * - When `thumbnailUrl` already starts with `http`, returns it as-is.
 * - When JWT authentication is enabled, appends the token as a `?token=`
 *   query parameter. <img src="..."> tags are loaded natively by the browser
 *   and cannot attach an Authorization header the way apiClient's fetch()
 *   calls do, so without this the backend's AuthMiddleware rejects every
 *   image request with 401 regardless of having a valid, logged-in session.
 *   See AuthMiddleware.cs's query-string token fallback.
 */
export const formatThumbnailUrl = (thumbnailUrl?: string): string => {
  const config = getApiConfig();
  if (!thumbnailUrl) {
    return "/rensaio.png";
  }
  if (thumbnailUrl.startsWith("http")) {
    return thumbnailUrl;
  }
  const url = `${config.baseUrl}${thumbnailUrl}`;
  if (typeof window === "undefined") {
    return url;
  }
  const token = sessionStorage.getItem("rensaio_token");
  if (!token) {
    return url;
  }
  const separator = url.includes("?") ? "&" : "?";
  return `${url}${separator}token=${encodeURIComponent(token)}`;
};
