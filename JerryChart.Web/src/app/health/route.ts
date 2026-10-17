// Liveness only: deliberately avoids the statistics API so a slow or restarting API cannot get the frontend killed.
export function GET() {
  return Response.json({ status: "Healthy" }, { headers: { "Cache-Control": "no-store" } });
}

export function HEAD() {
  return new Response(null, { headers: { "Cache-Control": "no-store" } });
}
