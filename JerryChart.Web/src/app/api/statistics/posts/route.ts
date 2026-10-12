import { apiUrl } from "@/lib/api";
import { isAllReplyPostList } from "@/lib/reply-posts";

export async function GET(request: Request) {
  try {
    const response = await fetch(apiUrl("/statistics/right-jerry/posts"), {
      cache: "no-store",
      signal: AbortSignal.any([request.signal, AbortSignal.timeout(10_000)]),
    });
    if (!response.ok) throw new Error(`Statistics API returned HTTP ${response.status}.`);
    const posts: unknown = await response.json();
    if (!isAllReplyPostList(posts)) throw new Error("Statistics API returned an invalid reply post list.");
    return Response.json(posts, { headers: { "Cache-Control": "no-store" } });
  } catch (error) {
    if (request.signal.aborted) return new Response(null, { status: 499 });
    console.error("Unable to load reply posts.", error);
    return Response.json({ error: "Unable to load reply posts." }, { status: 502 });
  }
}
