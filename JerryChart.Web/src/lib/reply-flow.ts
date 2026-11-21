import { isMonthlyReplyCount, type MonthlyReplyCount } from "@/lib/monthly-replies";

export interface ReplySummary {
  totalReplies: number;
  rightJerryReplies: number;
  wrongJerryReplies: number;
}

export interface ReplyFlow {
  summary: ReplySummary;
  months: MonthlyReplyCount[];
}

function isCount(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

export function isReplySummary(value: unknown): value is ReplySummary {
  return typeof value === "object" && value !== null &&
    "totalReplies" in value && isCount(value.totalReplies) &&
    "rightJerryReplies" in value && isCount(value.rightJerryReplies) &&
    "wrongJerryReplies" in value && isCount(value.wrongJerryReplies) &&
    value.rightJerryReplies + value.wrongJerryReplies === value.totalReplies;
}

export function isReplyFlow(value: unknown): value is ReplyFlow {
  if (typeof value !== "object" || value === null ||
      !("summary" in value) || !isReplySummary(value.summary) ||
      !("months" in value) || !Array.isArray(value.months) ||
      !value.months.every(isMonthlyReplyCount)) return false;
  const { months, summary } = value;
  return months.every((month, index) => month.replyCount > 0 &&
    (index === 0 || month.month > months[index - 1].month)) &&
    months.reduce((total, month) => total + month.replyCount, 0) === summary.rightJerryReplies;
}

export interface ReplyFlowNode {
  name: string;
  count: number;
  stage: "all" | "right" | "wrong" | "year" | "month";
}

export function buildReplyFlowData({ summary, months }: ReplyFlow) {
  const nodes: ReplyFlowNode[] = [{ name: "All replies", count: summary.totalReplies, stage: "all" }];
  const links: { source: number; target: number; value: number }[] = [];
  let rightIndex = 0;
  if (summary.rightJerryReplies > 0) {
    rightIndex = nodes.push({ name: "The Right Jerry", count: summary.rightJerryReplies, stage: "right" }) - 1;
    links.push({ source: 0, target: rightIndex, value: summary.rightJerryReplies });
  }
  if (summary.wrongJerryReplies > 0) {
    const target = nodes.push({ name: "The Wrong Jerry", count: summary.wrongJerryReplies, stage: "wrong" }) - 1;
    links.push({ source: 0, target, value: summary.wrongJerryReplies });
  }
  const years = new Map<string, MonthlyReplyCount[]>();
  for (const month of months) {
    const year = month.month.slice(0, 4);
    const group = years.get(year) ?? [];
    group.push(month);
    years.set(year, group);
  }
  const formatter = new Intl.DateTimeFormat("en-US", { month: "long", year: "numeric", timeZone: "UTC" });
  for (const [year, group] of years) {
    const count = group.reduce((total, month) => total + month.replyCount, 0);
    const yearIndex = nodes.push({ name: year, count, stage: "year" }) - 1;
    links.push({ source: rightIndex, target: yearIndex, value: count });
    for (const month of group) {
      const target = nodes.push({
        name: formatter.format(new Date(`${month.month}T00:00:00Z`)),
        count: month.replyCount,
        stage: "month",
      }) - 1;
      links.push({ source: yearIndex, target, value: month.replyCount });
    }
  }
  return { nodes, links };
}
