"use client";

import { lazy, Suspense, type ReactNode } from "react";
import { StatisticsDialog } from "@/components/statistics-dialog";
import { isReplyFlow } from "@/lib/reply-flow";

const ReplySankeyChart = lazy(() => import("@/components/reply-sankey-chart")
  .then((module) => ({ default: module.ReplySankeyChart })));

export function ReplyFlowDialog({ children }: { children: ReactNode }) {
  return (
    <StatisticsDialog
      title="All replies by Jerry, year, and month"
      triggerLabel="Show all replies by Jerry, year, and month"
      trigger={children}
      overlayTrigger
      path="/api/statistics/reply-flow"
      isValid={isReplyFlow}
    >
      {(flow) => (
        <Suspense fallback={<p role="status">Loading diagram...</p>}>
          <ReplySankeyChart flow={flow} />
        </Suspense>
      )}
    </StatisticsDialog>
  );
}
