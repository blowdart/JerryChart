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
        ? <a href={blueskyPostUrl(atUri)} className="underline underline-offset-4">
          <LocalizedTime timestamp={updatedAt} />
          <span className="sr-only"> (view the matching reply on Bluesky)</span>
        </a>
        : <LocalizedTime timestamp={updatedAt} />}
    </span>
  );
}
