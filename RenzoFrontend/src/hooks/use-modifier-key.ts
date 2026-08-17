"use client";

import { useEffect, useState } from "react";

/**
 * The label for THIS device's shortcut modifier: `⌘` on Apple hardware, `Ctrl`
 * everywhere else. The Cmd/Ctrl+K handler already accepts either key — this is
 * only about showing the one the user actually has.
 *
 * **Null until mounted, deliberately.** The frontend is a static export, so
 * anything platform-dependent evaluated at build time gets baked into the HTML
 * and then contradicted on hydration — React would warn, and every visitor
 * would be served whichever platform the build machine happened to be. Callers
 * render nothing while it is null; the hint is decorative and absolutely
 * positioned, so its absence for one frame shifts no layout and shows no flash
 * of the wrong key.
 */
export function useModifierKeyLabel(): string | null {
  const [label, setLabel] = useState<string | null>(null);

  useEffect(() => {
    if (typeof navigator === "undefined") return;

    // userAgentData.platform is the non-deprecated source but is Chromium-only,
    // so navigator.platform and the UA string back it up. Checked together
    // rather than in a chain of ifs: any one of them naming Apple hardware is
    // enough, and none of them names it spuriously on Windows or Linux.
    const uaData = (navigator as Navigator & { userAgentData?: { platform?: string } }).userAgentData;
    const haystack = [uaData?.platform, navigator.platform, navigator.userAgent]
      .filter(Boolean)
      .join(" ")
      .toLowerCase();

    // iPhone/iPad included: with a hardware keyboard attached they use ⌘ too,
    // and an iPad in desktop mode reports "macintosh" anyway.
    const isApple = /mac|iphone|ipad|ipod/.test(haystack);
    setLabel(isApple ? "⌘" : "Ctrl");
  }, []);

  return label;
}
