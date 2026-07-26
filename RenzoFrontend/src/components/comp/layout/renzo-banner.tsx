import React from "react";

/**
 * Full Renzo Shiori brand lockup (enso mark + wordmark) from the login banner.
 * Themed like {@link RenzoLogo}: ink version on light surfaces, light version on
 * dark surfaces via Tailwind class-based dark mode. Plain <img> — no theme hook,
 * no hydration flicker. Set the height via `className` (e.g. "h-8"); width auto.
 */
export function RenzoBanner({ className = "" }: { className?: string }) {
  return (
    <>
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img
        src="/renzo-login-banner-light.png?v=shiori"
        alt="Renzo Shiori"
        className={`block dark:hidden w-auto ${className}`}
        draggable={false}
      />
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img
        src="/renzo-login-banner-dark.png?v=shiori"
        alt="Renzo Shiori"
        aria-hidden="true"
        className={`hidden dark:block w-auto ${className}`}
        draggable={false}
      />
    </>
  );
}
