"use client";

import { useEffect, useState } from "react";

export function StatisticsLastUpdated({ updatedAt }: { updatedAt: string | null }) {
  const [localized, setLocalized] = useState<{ timestamp: string; text: string } | null>(null);

  useEffect(() => {
    if (updatedAt !== null) {
      setLocalized({
        timestamp: updatedAt,
        text: new Intl.DateTimeFormat(navigator.languages.length ? [...navigator.languages] : undefined, {
          dateStyle: "medium",
          timeStyle: "short",
        }).format(new Date(updatedAt)),
      });
    }
  }, [updatedAt]);

  if (updatedAt === null) {
    return <p>&quot;Jerry no&quot; last detected: not yet recorded</p>;
  }

  return (
    <p>
      &quot;Jerry no&quot; last detected:{" "}
      <time dateTime={updatedAt}>
        {localized?.timestamp === updatedAt ? localized.text : `${updatedAt} (UTC)`}
      </time>
    </p>
  );
}
