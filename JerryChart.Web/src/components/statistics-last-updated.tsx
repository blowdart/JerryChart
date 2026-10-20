"use client";

import { LocalizedTime } from "@/components/localized-time";
import { blueskyPostUrl } from "@/lib/reply-posts";

export function StatisticsLastUpdated({ updatedAt, atUri }: { updatedAt: string | null; atUri: string | null }) {
  if (updatedAt === null) {
    return <span className="block"><strong>&quot;Jerry No&quot;</strong> last detected: not yet recorded</span>;
  }

  return (
    <span className="block">
      <strong>&quot;Jerry No&quot;</strong> last detected:{" "}
      {atUri
        ? <a href={blueskyPostUrl(atUri)} aria-label="View the matching Jerry no reply on Bluesky"
          className="underline underline-offset-4">
          <LocalizedTime timestamp={updatedAt} />
        </a>
        : <LocalizedTime timestamp={updatedAt} />}
    </span>
  );
}
