import "server-only";
import { isMonthlyReplySeries, type MonthlyReplyCount } from "@/lib/monthly-replies";
import { isReplyAuthorList, type TopReplyAuthor } from "@/lib/reply-authors";
import { isReplyPostList, type TopReplyPost } from "@/lib/reply-posts";
import { isProcessingStatus, type ProcessingStatus } from "@/lib/processing-status";

export async function getProcessingStatus(signal?: AbortSignal): Promise<ProcessingStatus> {
  const response = await fetch(apiUrl("/statistics/processing-status"), {
    cache: "no-store",
    signal: signal
      ? AbortSignal.any([signal, AbortSignal.timeout(10_000)])
      : AbortSignal.timeout(10_000),
  });
  if (!response.ok) {
    throw new Error(`The API could not load processing status (HTTP ${response.status}).`);
  }
  const status: unknown = await response.json();
  if (!isProcessingStatus(status)) {
    throw new Error("The API returned an invalid processing status response.");
  }
  return status;
}

export async function getInitialProcessingStatus(): Promise<{
  status: ProcessingStatus | null;
  error: string | null;
}> {
  try {
    return { status: await getProcessingStatus(), error: null };
  } catch (error) {
    console.error("Unable to load processing status.", error);
    return { status: null, error: "Unable to load processing status. Worker activity is unknown." };
  }
}

export async function getTopRightJerryPosts(): Promise<TopReplyPost[]> {
  const response = await fetch(apiUrl("/statistics/right-jerry/top-posts"), {
    cache: "no-store",
    signal: AbortSignal.timeout(10_000),
  });
  if (!response.ok) {
    throw new Error(`The API could not load the top Jerry posts (HTTP ${response.status}).`);
  }

  const posts: unknown = await response.json();
  if (!isReplyPostList(posts)) {
    throw new Error("The API returned an invalid top Jerry posts response.");
  }
  return posts;
}

export async function getStatisticsLastUpdated(): Promise<string | null> {
  const response = await fetch(apiUrl("/statistics/last-updated"), {
    cache: "no-store",
    signal: AbortSignal.timeout(10_000),
  });
  if (!response.ok) {
    throw new Error(`The API could not load the statistics update time (HTTP ${response.status}).`);
  }

  const result: unknown = await response.json();
  if (
    typeof result !== "object" ||
    result === null ||
    !("updatedAt" in result) ||
    !(result.updatedAt === null || (
      typeof result.updatedAt === "string" &&
      /(?:Z|\+00:00)$/.test(result.updatedAt) &&
      Number.isFinite(Date.parse(result.updatedAt))
    ))
  ) {
    throw new Error("The API returned an invalid statistics update time.");
  }

  return result.updatedAt;
}

interface ReplySummary {
  totalReplies: number;
  rightJerryReplies: number;
  wrongJerryReplies: number;
}

export async function getMonthlyRightJerryReplies(): Promise<MonthlyReplyCount[]> {
  const response = await fetch(apiUrl("/statistics/right-jerry/monthly-replies"), {
    cache: "no-store",
    signal: AbortSignal.timeout(10_000),
  });
  if (!response.ok) {
    throw new Error(`The API could not load monthly replies (HTTP ${response.status}).`);
  }

  const months: unknown = await response.json();
  if (
    !isMonthlyReplySeries(months) || months.length !== 6
  ) {
    throw new Error("The API returned an invalid monthly replies response.");
  }

  return months;
}

export async function getTopRightJerryAuthors(): Promise<TopReplyAuthor[]> {
  const response = await fetch(apiUrl("/statistics/right-jerry/top-authors"), {
    cache: "no-store",
    signal: AbortSignal.timeout(10_000),
  });
  if (!response.ok) {
    throw new Error(`The API could not load the top reply authors (HTTP ${response.status}).`);
  }

  const authors: unknown = await response.json();
  if (
    !isReplyAuthorList(authors) ||
    authors.length > 10
  ) {
    throw new Error("The API returned an invalid top reply authors response.");
  }

  return authors;
}

function isCount(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

function isReplySummary(value: unknown): value is ReplySummary {
  return (
    typeof value === "object" &&
    value !== null &&
    "totalReplies" in value &&
    isCount(value.totalReplies) &&
    "rightJerryReplies" in value &&
    isCount(value.rightJerryReplies) &&
    "wrongJerryReplies" in value &&
    isCount(value.wrongJerryReplies) &&
    value.rightJerryReplies + value.wrongJerryReplies === value.totalReplies
  );
}

export async function getReplySummary(): Promise<ReplySummary> {
  const response = await fetch(apiUrl("/statistics/reply-summary"), {
    cache: "no-store",
    signal: AbortSignal.timeout(10_000),
  });
  if (!response.ok) {
    throw new Error(`The API could not load reply statistics (HTTP ${response.status}).`);
  }

  const summary: unknown = await response.json();
  if (!isReplySummary(summary)) {
    throw new Error("The API returned an invalid reply summary response.");
  }

  return summary;
}

export function apiUrl(path: string): URL {
  const baseUrl = process.env.API_BASE_URL;
  if (!baseUrl) {
    throw new Error("API_BASE_URL is required. Start the frontend through Aspire.");
  }

  return new URL(path, baseUrl);
}
