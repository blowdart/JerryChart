import { apiUrl } from "@/lib/api";
import { isReplyFlow } from "@/lib/reply-flow";

export async function GET(request: Request) {
  try {
    const response = await fetch(apiUrl("/statistics/reply-flow"), {
      cache: "no-store",
      signal: AbortSignal.any([request.signal, AbortSignal.timeout(10_000)]),
    });
    if (!response.ok) {
      throw new Error(`Statistics API returned HTTP ${response.status}.`);
    }
    const flow: unknown = await response.json();
    if (!isReplyFlow(flow)) {
      throw new Error("Statistics API returned an invalid reply flow.");
    }
    return Response.json(flow, { headers: { "Cache-Control": "no-store" } });
  } catch (error) {
    if (request.signal.aborted) return new Response(null, { status: 499 });
    console.error("Unable to load reply flow statistics.", error);
    return Response.json({ error: "Unable to load reply flow statistics." }, { status: 502 });
  }
}
