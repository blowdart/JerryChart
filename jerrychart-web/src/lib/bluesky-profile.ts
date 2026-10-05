export interface BlueskyProfile {
  did: string;
  handle: string;
  displayName?: string;
  description?: string;
  avatar?: string;
  followersCount?: number;
  followsCount?: number;
  postsCount?: number;
}

const cacheLifetime = 15 * 60 * 1000;
const cache = new Map<string, { profile: BlueskyProfile; expiresAt: number }>();
const pending = new Map<string, Promise<BlueskyProfile>>();

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isProfile(value: unknown, did: string): value is BlueskyProfile {
  if (!isRecord(value) || value.did !== did ||
      typeof value.handle !== "string" || value.handle.length === 0) {
    return false;
  }

  for (const field of ["displayName", "description", "avatar"] as const) {
    if (field in value && typeof value[field] !== "string") return false;
  }
  for (const field of ["followersCount", "followsCount", "postsCount"] as const) {
    if (field in value && (typeof value[field] !== "number" ||
        !Number.isSafeInteger(value[field]) || value[field] < 0)) return false;
  }
  return true;
}

export function profileAvatarUrl(avatar: string | undefined): string | undefined {
  if (!avatar) return undefined;
  try {
    const url = new URL(avatar);
    return url.protocol === "https:" && url.hostname === "cdn.bsky.app" &&
      !url.username && !url.password ? url.href : undefined;
  } catch {
    return undefined;
  }
}

export function getBlueskyProfile(did: string): Promise<BlueskyProfile> {
  const cached = cache.get(did);
  if (cached && cached.expiresAt > Date.now()) return Promise.resolve(cached.profile);
  cache.delete(did);
  const existing = pending.get(did);
  if (existing) return existing;

  const request = (async () => {
    const url = new URL("https://public.api.bsky.app/xrpc/app.bsky.actor.getProfile");
    url.searchParams.set("actor", did);
    const response = await fetch(url, {
      credentials: "omit",
      cache: "no-store",
      signal: AbortSignal.timeout(10_000),
    });
    if (!response.ok) throw new Error(`Bluesky profile request failed (HTTP ${response.status}).`);
    const profile: unknown = await response.json();
    if (!isProfile(profile, did)) throw new Error("Bluesky returned an invalid profile.");
    for (const [key, entry] of cache) {
      if (entry.expiresAt <= Date.now()) cache.delete(key);
    }
    if (cache.size >= 250) {
      const oldest = cache.keys().next().value;
      if (oldest !== undefined) cache.delete(oldest);
    }
    cache.set(did, { profile, expiresAt: Date.now() + cacheLifetime });
    return profile;
  })().finally(() => pending.delete(did));
  pending.set(did, request);
  return request;
}
