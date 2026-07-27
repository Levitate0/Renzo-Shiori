/**
 * Adapters that wrap a shell's raw JS interface (Android @JavascriptInterface
 * `window.__RenzoAndroid`, desktop WebView2 host object `window.__RenzoWindows`)
 * into the async {@link NativePrimitives} contract. Keeping the adapter in the
 * frontend means the shells stay minimal (expose synchronous file/KV/network
 * methods) and there's no script-injection timing race — the raw interface is
 * present from page load.
 */
import type { NativePlatform, NativePrimitives } from "./types";

/** Synchronous methods a native shell exposes. Strings only (binary as base64). */
export interface RawNativeInterface {
  writeFileB64(relPath: string, b64: string): void;
  readFileB64(relPath: string): string;
  deletePath(relPath: string): void;
  exists(relPath: string): boolean;
  kvGet(key: string): string | null;
  kvSet(key: string, value: string): void;
  isOnline(): boolean;
  /** Triggers the native folder picker; result arrives as a `renzo:folderpicked` event. */
  pickFolder(): void;
  getFolder(): string | null;
}

function abToB64(data: ArrayBuffer): string {
  const bytes = new Uint8Array(data);
  let bin = "";
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    bin += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }
  return btoa(bin);
}

function mimeFromPath(p: string): string {
  const ext = (p.split(".").pop() ?? "").toLowerCase();
  if (ext === "png") return "image/png";
  if (ext === "webp") return "image/webp";
  if (ext === "gif") return "image/gif";
  if (ext === "avif") return "image/avif";
  return "image/jpeg";
}

/** Wrap a raw shell interface (same method shape on Android + desktop). */
export function interfaceAdapter(raw: RawNativeInterface, platform: Exclude<NativePlatform, "web">): NativePrimitives {
  return {
    platform,
    pickFolder: () =>
      new Promise<string | null>((resolve) => {
        const handler = (e: Event) => {
          window.removeEventListener("renzo:folderpicked", handler);
          const label = (e as CustomEvent<{ label?: string | null }>).detail?.label;
          resolve(typeof label === "string" && label.length > 0 ? label : null);
        };
        window.addEventListener("renzo:folderpicked", handler);
        raw.pickFolder();
      }),
    getFolder: async () => raw.getFolder(),
    writeFile: async (relPath, data) => {
      raw.writeFileB64(relPath, abToB64(data));
    },
    readFileSrc: async (relPath) => `data:${mimeFromPath(relPath)};base64,${raw.readFileB64(relPath)}`,
    deletePath: async (relPath) => {
      raw.deletePath(relPath);
    },
    exists: async (relPath) => raw.exists(relPath),
    kvGet: async (key) => raw.kvGet(key),
    kvSet: async (key, value) => {
      raw.kvSet(key, value);
    },
    isOnline: async () => raw.isOnline(),
    onNetworkChange: (cb) => {
      const handler = (e: Event) => cb(Boolean((e as CustomEvent<{ online?: boolean }>).detail?.online));
      window.addEventListener("renzo:netchange", handler);
      return () => window.removeEventListener("renzo:netchange", handler);
    },
  };
}
