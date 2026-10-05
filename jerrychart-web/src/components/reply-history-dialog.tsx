"use client";

import { MonthlyRepliesChart } from "@/components/monthly-replies-chart";
import { StatisticsDialog } from "@/components/statistics-dialog";
import { isMonthlyReplySeries, type MonthlyReplyCount } from "@/lib/monthly-replies";

export function ReplyHistoryDialog({ months }: { months: MonthlyReplyCount[] }) {
  return (
    <StatisticsDialog
      title={'Correct "Jerry No" replies, all time'}
      triggerLabel="Show correct Jerry No replies over all time"
      trigger={<MonthlyRepliesChart months={months} />}
      path="/api/statistics/all-time-monthly-replies"
      isValid={isMonthlyReplySeries}
    >
      {(history) => history.length === 0 ? (
        <p>No matching replies to the correct Jerry recorded yet.</p>
      ) : (
        <MonthlyRepliesChart months={history} expanded />
      )}
    </StatisticsDialog>
  );
}
