export interface MonthlyReplyCount {
  month: string;
  replyCount: number;
}

export function isMonthlyReplyCount(value: unknown): value is MonthlyReplyCount {
  return typeof value === "object" && value !== null &&
    "month" in value && typeof value.month === "string" &&
    /^\d{4}-(0[1-9]|1[0-2])-01$/.test(value.month) &&
    "replyCount" in value && typeof value.replyCount === "number" &&
    Number.isSafeInteger(value.replyCount) && value.replyCount >= 0;
}

export function isMonthlyReplySeries(value: unknown): value is MonthlyReplyCount[] {
  if (!Array.isArray(value) || !value.every(isMonthlyReplyCount)) return false;
  return value.every((item, index) => {
    if (index === 0) return true;
    const previous = new Date(`${value[index - 1].month}T00:00:00Z`);
    previous.setUTCMonth(previous.getUTCMonth() + 1);
    return previous.toISOString().slice(0, 10) === item.month;
  });
}
