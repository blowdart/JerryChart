export interface MonthlyReplyCount {
  month: string;
  replyCount: number;
}

export function isMonthlyReplySeries(value: unknown): value is MonthlyReplyCount[] {
  if (!Array.isArray(value)) return false;
  return value.every((item, index) => {
    if (
      typeof item !== "object" || item === null ||
      typeof item.month !== "string" || !/^\d{4}-(0[1-9]|1[0-2])-01$/.test(item.month) ||
      typeof item.replyCount !== "number" || !Number.isSafeInteger(item.replyCount) || item.replyCount < 0
    ) return false;
    if (index === 0) return true;
    const previous = new Date(`${value[index - 1].month}T00:00:00Z`);
    previous.setUTCMonth(previous.getUTCMonth() + 1);
    return previous.toISOString().slice(0, 10) === item.month;
  });
}
