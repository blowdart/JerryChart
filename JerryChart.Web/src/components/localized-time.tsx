"use client";

import { useSyncExternalStore } from "react";

const subscribe = () => () => {};
const clientSnapshot = () => true;
const serverSnapshot = () => false;

export function LocalizedTime({ timestamp }: { timestamp: string | null }) {
  const hydrated = useSyncExternalStore(subscribe, clientSnapshot, serverSnapshot);
  if (timestamp === null) return <>not recorded</>;
  const text = hydrated
    ? new Intl.DateTimeFormat(navigator.languages.length ? [...navigator.languages] : undefined, {
      dateStyle: "medium",
      timeStyle: "short",
    }).format(new Date(timestamp))
    : `${timestamp} (UTC)`;

  return <time dateTime={timestamp}>{text}</time>;
}
