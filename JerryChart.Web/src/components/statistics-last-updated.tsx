"use client";

import { LocalizedTime } from "@/components/localized-time";
import { PostHoverCard } from "@/components/post-hover-card";

export function StatisticsLastUpdated({ updatedAt, atUri }: { updatedAt: string | null; atUri: string | null }) {
  if (updatedAt === null) {
    return <span className="block"><strong>&quot;Jerry No&quot;</strong> last detected: not yet recorded</span>;
  }

  return (
    <span className="block">
      <strong>&quot;Jerry No&quot;</strong> last detected:{" "}
      {atUri
        ? <PostHoverCard key={atUri} atUri={atUri}>
          <LocalizedTime timestamp={updatedAt} />
          <span className="sr-only"> (view the matching reply on Bluesky)</span>
        </PostHoverCard>
        : <LocalizedTime timestamp={updatedAt} />}
    </span>
  );
}
