"use client";

import { LocalizedTime } from "@/components/localized-time";

export function StatisticsLastUpdated({ updatedAt }: { updatedAt: string | null }) {
  if (updatedAt === null) {
    return <span className="block"><strong>&quot;Jerry No&quot;</strong> last detected: not yet recorded</span>;
  }

  return (
    <span className="block">
      <strong>&quot;Jerry No&quot;</strong> last detected:{" "}
      <LocalizedTime timestamp={updatedAt} />
    </span>
  );
}
