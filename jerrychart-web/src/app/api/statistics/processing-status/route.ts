import { getProcessingStatus } from "@/lib/api";

export async function GET(request: Request) {
  try {
    return Response.json(await getProcessingStatus(request.signal), {
      headers: { "Cache-Control": "no-store" },
    });
  } catch (error) {
    if (request.signal.aborted) return new Response(null, { status: 499 });
    console.error("Unable to load processing status.", error);
    return Response.json({ error: "Unable to load processing status." }, {
      status: 502,
      headers: { "Cache-Control": "no-store" },
    });
  }
}
