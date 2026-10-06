export interface TopReplyAuthor {
  did: string;
  handle: string | null;
  accountStatus?: string | null;
  replyCount: number;
}

function isReplyAuthor(value: unknown): value is TopReplyAuthor {
  return (
    typeof value === "object" &&
    value !== null &&
    "did" in value &&
    typeof value.did === "string" &&
    value.did.startsWith("did:") &&
    "handle" in value &&
    (value.handle === null || (typeof value.handle === "string" && value.handle.length > 0)) &&
    (!("accountStatus" in value) || value.accountStatus === null ||
      (typeof value.accountStatus === "string" &&
        ["deleted", "suspended", "deactivated", "takendown", "desynchronized", "throttled", "inactive"].includes(value.accountStatus))) &&
    "replyCount" in value &&
    typeof value.replyCount === "number" &&
    Number.isSafeInteger(value.replyCount) &&
    value.replyCount > 0
  );
}

export function authorLabel(author: Pick<TopReplyAuthor, "handle" | "accountStatus">): string {
  if (author.handle) return `@${author.handle}`;
  switch (author.accountStatus) {
    case "deleted": return "Deleted";
    case "suspended": return "Suspended";
    case "deactivated": return "Deactivated";
    case "takendown": return "Taken down";
    case "desynchronized": return "Desynchronized";
    case "throttled": return "Throttled";
    case "inactive": return "Inactive";
    default: return "Unresolved";
  }
}

export function isReplyAuthorList(value: unknown): value is TopReplyAuthor[] {
  return (
    Array.isArray(value) &&
    value.every(isReplyAuthor) &&
    new Set(value.map((author) => author.did)).size === value.length &&
    value.every((author, index) => index === 0 || author.replyCount <= value[index - 1].replyCount)
  );
}
