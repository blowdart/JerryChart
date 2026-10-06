import { apiUrl } from "@/lib/api";
import { isMonthlyReplySeries } from "@/lib/monthly-replies";

export async function GET(request: Request) {
  try {
    const response = await fetch(apiUrl("/statistics/right-jerry/all-time-monthly-replies"), {
      cache: "no-store",
      signal: AbortSignal.any([request.signal, AbortSignal.timeout(10_000)]),
    });
    if (!response.ok) {
      throw new Error(`Statistics API returned HTTP ${response.status}.`);
    }
    const months: unknown = await response.json();
    if (!isMonthlyReplySeries(months)) {
      throw new Error("Statistics API returned an invalid monthly series.");
    }
    return Response.json(months, { headers: { "Cache-Control": "no-store" } });
  } catch (error) {
    if (request.signal.aborted) return new Response(null, { status: 499 });
    console.error("Unable to load all-time reply statistics.", error);
    return Response.json({ error: "Unable to load all-time reply statistics." }, { status: 502 });
  }
}
