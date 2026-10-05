import { isRightJerryPostUri, rightJerryDid } from "@/lib/reply-posts";

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export async function getBlueskyPosts(
  atUris: string[],
  signal: AbortSignal,
): Promise<Record<string, string>> {
  if (atUris.length > 5 || !atUris.every(isRightJerryPostUri) ||
      new Set(atUris).size !== atUris.length) {
    throw new Error("Invalid Jerry post request.");
  }
  if (atUris.length === 0) return {};

  const url = new URL("https://public.api.bsky.app/xrpc/app.bsky.feed.getPosts");
  for (const uri of atUris) url.searchParams.append("uris", uri);
  const response = await fetch(url, {
    credentials: "omit",
    cache: "no-store",
    signal: AbortSignal.any([signal, AbortSignal.timeout(10_000)]),
  });
  if (!response.ok) throw new Error(`Bluesky post request failed (HTTP ${response.status}).`);
  const result: unknown = await response.json();
  if (!isRecord(result) || !Array.isArray(result.posts)) {
    throw new Error("Bluesky returned an invalid posts response.");
  }

  const texts: Record<string, string> = {};
  for (const post of result.posts) {
    if (!isRecord(post) || typeof post.uri !== "string" || !atUris.includes(post.uri) ||
        Object.hasOwn(texts, post.uri) || !isRecord(post.author) || post.author.did !== rightJerryDid ||
        !isRecord(post.record) || typeof post.record.text !== "string") {
      throw new Error("Bluesky returned an invalid parent post.");
    }
    texts[post.uri] = post.record.text;
  }
  return texts;
}
