"use client";
import * as React from "react";

/**
 * A multi-line paste into a single-line text box arrives as one line: each run
 * of line breaks (and the spaces around it) becomes a single space. Browsers
 * otherwise just drop the breaks, so a title copied as "Title\nSubtitle"
 * landed as "TitleSubtitle" and searched for nothing. One handler for every
 * <input> on the page; textareas and password fields are left alone. The text
 * goes in through insertText, so React's onChange sees a normal edit and undo
 * still works. Hub twin: ui/util/OneLine.kt.
 */
const SINGLE_LINE = /^(text|search|url|email|tel|)$/i;

export function oneLine(text: string): string {
  return /[\r\n]/.test(text) ? text.replace(/\s*[\r\n]+\s*/g, " ").trim() : text;
}

export function OneLinePaste() {
  React.useEffect(() => {
    const onPaste = (e: ClipboardEvent) => {
      const el = e.target;
      if (!(el instanceof HTMLInputElement) || !SINGLE_LINE.test(el.type)) return;
      const text = e.clipboardData?.getData("text/plain");
      if (!text || !/[\r\n]/.test(text)) return;
      e.preventDefault();
      const flat = oneLine(text);
      if (!document.execCommand("insertText", false, flat)) {
        // execCommand unavailable: splice the value and tell React about it.
        const start = el.selectionStart ?? el.value.length;
        const end = el.selectionEnd ?? el.value.length;
        const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")?.set;
        setter?.call(el, el.value.slice(0, start) + flat + el.value.slice(end));
        el.setSelectionRange(start + flat.length, start + flat.length);
        el.dispatchEvent(new Event("input", { bubbles: true }));
      }
    };
    document.addEventListener("paste", onPaste, true);
    return () => document.removeEventListener("paste", onPaste, true);
  }, []);
  return null;
}
