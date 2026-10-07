"use client";

import { useEffect, useState } from "react";
import { LinkifyIt } from "linkify-it";
import tlds from "tlds";
import { isBlueskyHandle, resolveBlueskyHandle } from "@/lib/bluesky-handle";

// Recognize emails to reserve their full span, but never render them as links.
const linkify = new LinkifyIt({ fuzzyLink: true, fuzzyIP: false, urlAuth: true, tlds });

function ProfileMention({ handle }: { handle: string }) {
  const [resolution, setResolution] = useState<{ handle: string; did: string | null } | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let active = true;
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
  const links = linkify.match(text) ?? [];
  const tokens = [
    ...links.map((match) => ({
      start: match.index, end: match.lastIndex, text: match.raw,
      href: match.schema === "" ? `https://${match.raw}` : match.url,
      schema: match.schema,
    })),
    ...Array.from(text.matchAll(/@[\p{L}\p{N}_.-]+/giu))
      .filter((match) => !links.some((link) => match.index < link.lastIndex &&
        match.index + match[0].length > link.index))
      .map((match) => ({
        start: match.index, end: match.index + match[0].length,
        text: match[0], href: "", schema: "@",
      })),
  ].sort((left, right) => left.start - right.start);
  for (const token of tokens) {
    const start = token.start;
    if (token.schema === "@") {
      const handle = token.text.slice(1).replace(/\.+$/, "");
      if ((start > 0 && /[\p{L}\p{N}_.+@-]/u.test(text[start - 1])) ||
          !isBlueskyHandle(handle)) continue;
      parts.push(text.slice(offset, start));
      parts.push(<ProfileMention key={`${start}:${handle}`} handle={handle} />);
      offset = start + handle.length + 1;
      continue;
    }
    if (!["", "http:", "https:"].includes(token.schema)) continue;
    try {
      const url = new URL(token.href);
      if (!["http:", "https:"].includes(url.protocol) ||
          !url.hostname || url.username || url.password) continue;
    } catch {
      continue;
    }
    parts.push(text.slice(offset, start));
    parts.push(
      <a
        key={start}
        href={token.href}
        target="_blank"
        rel="noopener noreferrer"
        className="underline underline-offset-4"
      >
        {token.text}
      </a>,
    );
    offset = token.end;
  }
  parts.push(text.slice(offset));
  return <>{parts}</>;
}
