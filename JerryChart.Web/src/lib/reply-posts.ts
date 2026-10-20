export const rightJerryDid = "did:plc:vc7f4oafdgxsihk4cry2xpze";
const postPrefix = `at://${rightJerryDid}/app.bsky.feed.post/`;
const didSegment = "(?:[A-Za-z0-9._-]|%[A-Fa-f0-9]{2})+";
const postUriPattern = new RegExp(
  `^at://(did:[a-z0-9]+:${didSegment}(?::${didSegment})*)/app\\.bsky\\.feed\\.post/([A-Za-z0-9._~:-]{1,512})$`,
);

export interface TopReplyPost {
  atUri: string;
  replyCount: number;
}

export function isRightJerryPostUri(value: unknown): value is string {
  if (typeof value !== "string" || !value.startsWith(postPrefix)) return false;
  const key = value.slice(postPrefix.length);
  return /^[A-Za-z0-9._~:-]{1,512}$/.test(key) && key !== "." && key !== "..";
}

export function isBlueskyPostUri(value: unknown): value is string {
  if (typeof value !== "string") return false;
  const match = postUriPattern.exec(value);
  return match !== null && match[1] !== "" && match[2] !== "." && match[2] !== "..";
}

export function isReplyPostList(value: unknown): value is TopReplyPost[] {
  return Array.isArray(value) && value.length <= 5 && isAllReplyPostList(value);
}

export function isAllReplyPostList(value: unknown): value is TopReplyPost[] {
  return Array.isArray(value) &&
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
  const match = postUriPattern.exec(atUri);
  if (match === null || match[2] === "." || match[2] === "..") {
    throw new Error("Invalid Bluesky post URI.");
  }

  return `https://bsky.app/profile/${match[1]}/post/${match[2]}`;
}
