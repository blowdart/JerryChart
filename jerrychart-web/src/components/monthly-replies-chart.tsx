import type { MonthlyReplyCount } from "@/lib/monthly-replies";

export function MonthlyRepliesChart({ months, expanded = false }: { months: MonthlyReplyCount[]; expanded?: boolean }) {
  const maximum = months.reduce((highest, month) => Math.max(highest, month.replyCount), 1);
  const labelFormatter = new Intl.DateTimeFormat("en-US", {
    month: "short",
    year: "numeric",
    timeZone: "UTC",
  });

  return (
    <figure aria-label="Monthly Jerry no replies to the right Jerry">
      <div className={expanded ? "flex gap-4 overflow-x-auto pb-4" : "grid grid-cols-6 gap-2 sm:gap-4"}>
        {months.map((month) => {
          const label = labelFormatter.format(new Date(`${month.month}T00:00:00Z`));
          const count = month.replyCount.toLocaleString("en-US");
          return (
            <div key={month.month} className={expanded ? "w-20 shrink-0 text-center" : "min-w-0 text-center"}>
              <div className="flex h-56 flex-col justify-end">
                <span className="mb-2 text-xs font-medium tabular-nums sm:text-sm">{count}</span>
                <div
                  className="mx-auto w-3/4 shrink-0 rounded-t bg-primary"
                  style={{ height: `${(month.replyCount / maximum) * 180}px` }}
                  aria-hidden="true"
                />
              </div>
              <div className="border-t pt-2 text-xs sm:text-sm">
                <time dateTime={month.month}>{label}</time>
              </div>
              <span className="sr-only">{count} replies</span>
            </div>
          );
        })}
      </div>
      <figcaption className="sr-only">
        Monthly replies to the right Jerry, by post creation month in UTC.
        {months.every((month) => month.replyCount === 0) && (
          <span className="block">No matching replies recorded in these six months.</span>
        )}
      </figcaption>
    </figure>
  );
}
