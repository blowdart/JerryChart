"use client";

import { useEffect, useState } from "react";
import { isBlueskyHandle, resolveBlueskyHandle } from "@/lib/bluesky-handle";

function ProfileMention({ handle }: { handle: string }) {
  const [resolution, setResolution] = useState<{ handle: string; did: string | null } | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let active = true;
    setFailed(false);
    void resolveBlueskyHandle(handle).then(
      (did) => { if (active) setResolution({ handle, did }); },
      (error: unknown) => {
        console.error(`Unable to resolve Bluesky handle ${handle}.`, error);
        if (active) setFailed(true);
      },
    );
    return () => { active = false; };
  }, [handle]);

  if (resolution?.handle !== handle || resolution.did === null) {
    return <span title={failed ? "Unable to verify this Bluesky account." : undefined}>@{handle}</span>;
  }
  return (
    <a
      href={`https://bsky.app/profile/${resolution.did}`}
      target="_blank"
      rel="noopener noreferrer"
      className="underline underline-offset-4"
    >
      @{handle}
    </a>
  );
}

export function ProfileText({ text }: { text: string }) {
  const parts = [];
  let offset = 0;
  for (const match of text.matchAll(/https?:\/\/[^\s<>"'“”‘’]+|@[\p{L}\p{N}_.-]+/giu)) {
    const start = match.index;
    if (match[0].startsWith("@")) {
      const handle = match[0].slice(1).replace(/\.+$/, "");
      if ((start > 0 && /[\p{L}\p{N}_.+@-]/u.test(text[start - 1])) ||
          !isBlueskyHandle(handle)) continue;
      parts.push(text.slice(offset, start));
      parts.push(<ProfileMention key={start} handle={handle} />);
      offset = start + handle.length + 1;
      continue;
    }
    let link = match[0].replace(/[.,!?;:]+$/, "");
    for (const [opening, closing] of [["(", ")"], ["[", "]"], ["{", "}"]]) {
      while (link.endsWith(closing) &&
          link.split(closing).length > link.split(opening).length) {
        link = link.slice(0, -1);
      }
    }
    try {
      const url = new URL(link);
      if (!url.hostname || url.username || url.password) continue;
    } catch {
      continue;
    }
    parts.push(text.slice(offset, start));
    parts.push(
      <a
        key={start}
        href={link}
        target="_blank"
        rel="noopener noreferrer"
        className="underline underline-offset-4"
      >
        {link}
      </a>,
    );
    offset = start + link.length;
  }
  parts.push(text.slice(offset));
  return <>{parts}</>;
}
