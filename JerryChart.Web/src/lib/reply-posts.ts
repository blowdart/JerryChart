export const rightJerryDid = "did:plc:vc7f4oafdgxsihk4cry2xpze";
const postPrefix = `at://${rightJerryDid}/app.bsky.feed.post/`;

export interface TopReplyPost {
  atUri: string;
  replyCount: number;
}

export function isRightJerryPostUri(value: unknown): value is string {
  if (typeof value !== "string" || !value.startsWith(postPrefix)) return false;
  const key = value.slice(postPrefix.length);
  return /^[A-Za-z0-9._~:-]{1,512}$/.test(key) && key !== "." && key !== "..";
}

export function isReplyPostList(value: unknown): value is TopReplyPost[] {
  return Array.isArray(value) && value.length <= 5 &&
    value.every((post) =>
      typeof post === "object" && post !== null &&
      isRightJerryPostUri(post.atUri) &&
      typeof post.replyCount === "number" &&
      Number.isSafeInteger(post.replyCount) && post.replyCount > 0
    ) &&
    new Set(value.map((post) => post.atUri)).size === value.length &&
    value.every((post, index) => index === 0 ||
      post.replyCount < value[index - 1].replyCount ||
      (post.replyCount === value[index - 1].replyCount && post.atUri > value[index - 1].atUri));
}

export function blueskyPostUrl(atUri: string): string {
  if (!isRightJerryPostUri(atUri)) throw new Error("Invalid Jerry post URI.");
  return `https://bsky.app/profile/${rightJerryDid}/post/${atUri.slice(postPrefix.length)}`;
}
