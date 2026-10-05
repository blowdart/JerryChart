const cache = new Map<string, { did: string | null; expiresAt: number }>();
const pending = new Map<string, Promise<string | null>>();
const cacheLifetime = 15 * 60 * 1000;

export function isBlueskyHandle(handle: string): boolean {
  if (handle.length > 253) return false;
  const labels = handle.split(".");
  return labels.length >= 2 &&
    /^[a-z]/i.test(labels[labels.length - 1]) &&
    labels.every((label) => label.length >= 1 && label.length <= 63 &&
      /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/i.test(label));
}

export function resolveBlueskyHandle(handle: string): Promise<string | null> {
  if (!isBlueskyHandle(handle)) throw new Error("Invalid Bluesky handle.");
  const key = handle.toLowerCase();
  const cached = cache.get(key);
  if (cached && cached.expiresAt > Date.now()) return Promise.resolve(cached.did);
  cache.delete(key);
  const existing = pending.get(key);
  if (existing) return existing;

  const request = (async () => {
    const url = new URL("https://public.api.bsky.app/xrpc/com.atproto.identity.resolveHandle");
    url.searchParams.set("handle", key);
    const response = await fetch(url, {
      credentials: "omit",
      cache: "no-store",
      signal: AbortSignal.timeout(10_000),
    });
    const result: unknown = await response.json();
    let did: string | null;
    if (response.status === 400 && typeof result === "object" && result !== null &&
        "error" in result && result.error === "HandleNotFound") {
      did = null;
    } else {
      if (!response.ok) throw new Error(`Bluesky handle lookup failed (HTTP ${response.status}).`);
      if (typeof result !== "object" || result === null ||
          !("did" in result) || typeof result.did !== "string" ||
          !/^did:[a-z]+:[a-zA-Z0-9._:%-]+$/.test(result.did)) {
        throw new Error("Bluesky returned an invalid handle resolution.");
      }
      did = result.did;
    }
    for (const [name, entry] of cache) {
      if (entry.expiresAt <= Date.now()) cache.delete(name);
    }
    if (cache.size >= 250) {
      const oldest = cache.keys().next().value;
      if (oldest !== undefined) cache.delete(oldest);
    }
    cache.set(key, { did, expiresAt: Date.now() + cacheLifetime });
    return did;
  })().finally(() => pending.delete(key));
  pending.set(key, request);
  return request;
}
