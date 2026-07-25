import React from "react";

/**
 * Themed Renzō mark. Renders the ink (dark-strokes) version on light surfaces and the
 * light version on dark surfaces via Tailwind's class-based dark mode, so the torii
 * always contrasts its background. Plain <img> (SVG) — no JS/theme-hook, no hydration
 * flicker.
 */
export function RenzoLogo({ className = "" }: { className?: string }) {
  return (
    <>
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img
        src="/renzo-logo-light.png"
        alt="Renzō"
        className={`block dark:hidden ${className}`}
        draggable={false}
      />
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img
        src="/renzo-logo-dark.png"
        alt="Renzō"
        aria-hidden="true"
        className={`hidden dark:block ${className}`}
        draggable={false}
      />
    </>
  );
}
