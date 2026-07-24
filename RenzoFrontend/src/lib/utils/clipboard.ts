/**
 * Copy text to the clipboard, resilient to environments where the async
 * Clipboard API isn't available. `navigator.clipboard` is only exposed in
 * secure contexts (HTTPS / localhost) and can be undefined behind some reverse
 * proxies or in older/embedded browsers — touching `.writeText` on it then
 * throws "Cannot read properties of undefined". Falls back to the legacy
 * execCommand('copy') path in that case.
 *
 * Returns true on success, false if every method failed (caller decides whether
 * to surface that — e.g. tell the user to copy manually).
 */
export async function copyToClipboard(text: string): Promise<boolean> {
  // Preferred path: async Clipboard API, only when actually present.
  if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Permission denied / not focused / insecure context — fall through.
    }
  }

  // Legacy fallback: a hidden textarea + document.execCommand('copy').
  if (typeof document !== 'undefined') {
    try {
      const textarea = document.createElement('textarea');
      textarea.value = text;
      // Keep it out of view and non-disruptive to scroll/layout.
      textarea.setAttribute('readonly', '');
      textarea.style.position = 'fixed';
      textarea.style.top = '-9999px';
      textarea.style.opacity = '0';
      document.body.appendChild(textarea);
      textarea.select();
      textarea.setSelectionRange(0, text.length);
      const ok = document.execCommand('copy');
      document.body.removeChild(textarea);
      return ok;
    } catch {
      return false;
    }
  }

  return false;
}
