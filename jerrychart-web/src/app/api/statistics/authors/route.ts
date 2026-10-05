import { apiUrl } from "@/lib/api";
import { isReplyAuthorList } from "@/lib/reply-authors";

export async function GET(request: Request) {
  try {
    const response = await fetch(apiUrl("/statistics/right-jerry/authors"), {
      cache: "no-store",
      signal: AbortSignal.any([request.signal, AbortSignal.timeout(10_000)]),
    });
    if (!response.ok) {
      throw new Error(`Statistics API returned HTTP ${response.status}.`);
    }
    const authors: unknown = await response.json();
    if (!isReplyAuthorList(authors)) {
      throw new Error("Statistics API returned an invalid reply author list.");
    }
    return Response.json(authors, { headers: { "Cache-Control": "no-store" } });
  } catch (error) {
    if (request.signal.aborted) return new Response(null, { status: 499 });
    console.error("Unable to load reply authors.", error);
    return Response.json({ error: "Unable to load reply authors." }, { status: 502 });
  }
}
