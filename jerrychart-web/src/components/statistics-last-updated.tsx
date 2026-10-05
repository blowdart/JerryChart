"use client";

import { LocalizedTime } from "@/components/localized-time";

export function StatisticsLastUpdated({ updatedAt }: { updatedAt: string | null }) {
  if (updatedAt === null) {
    return <p>&quot;Jerry no&quot; last detected: not yet recorded</p>;
  }

  return (
    <p>
      &quot;Jerry no&quot; last detected:{" "}
      <LocalizedTime timestamp={updatedAt} />
    </p>
  );
}
