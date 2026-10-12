"use client";

import { useEffect, useState } from "react";
import { LoaderCircle } from "lucide-react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { getBlueskyPosts } from "@/lib/bluesky-posts";
import { blueskyPostUrl, isAllReplyPostList, type TopReplyPost } from "@/lib/reply-posts";
import { StatisticsDialog } from "@/components/statistics-dialog";

type PostState = {
  list: string;
  attempt: number;
  status: "loading" | "error" | "ready";
  texts: Record<string, string>;
  loaded: number;
};

type CachedPosts = Pick<PostState, "list" | "texts" | "loaded">;
type PostCache = Map<string, CachedPosts>;

export function TopReplyPosts({ posts, expanded = false, postCache }: {
  posts: TopReplyPost[];
  expanded?: boolean;
  postCache?: PostCache;
}) {
  const [expandedCache] = useState<PostCache>(() => new Map());
  const list = JSON.stringify(posts.map((post) => post.atUri));
  const [state, setState] = useState<PostState>(() => {
    const cached = postCache?.get(list);
    return cached?.list === list
      ? { ...cached, attempt: 0, status: cached.loaded === posts.length ? "ready" : "loading" }
      : { list, attempt: 0, status: "loading", texts: {}, loaded: 0 };
  });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      const uris: string[] = JSON.parse(list);
      const cached = postCache?.get(list);
      let texts: Record<string, string> = cached?.list === list ? cached.texts : {};
      let loaded = cached?.list === list ? cached.loaded : 0;
      try {
        setState({ list, attempt, texts, loaded, status: loaded === uris.length ? "ready" : "loading" });
        for (let offset = loaded; offset < uris.length; offset += 5) {
          const batch = await getBlueskyPosts(uris.slice(offset, offset + 5), controller.signal);
          if (controller.signal.aborted) return;
          texts = { ...texts, ...batch };
          loaded = Math.min(offset + 5, uris.length);
          if (postCache) {
            postCache.clear();
            postCache.set(list, { list, texts, loaded });
          }
          setState({ list, attempt, texts, loaded, status: loaded === uris.length ? "ready" : "loading" });
        }
      } catch (failure: unknown) {
        if (controller.signal.aborted) return;
        console.error("Unable to load Jerry's Bluesky posts.", failure);
        setState({ list, attempt, status: "error", texts, loaded });
      }
    })();
    return () => controller.abort();
  }, [list, attempt, postCache]);

  const current: PostState = state.list === list && state.attempt === attempt ? state
    : { list, attempt, status: "loading", texts: {}, loaded: 0 };
  const content = (
      <div className="space-y-3">
        {posts.length === 0 ? (
          <p className="text-sm text-muted-foreground">No recorded replies to resolved Jerry posts yet.</p>
        ) : (
          <>
            {current.status === "loading" && (
              <p role="status" className="flex items-center gap-2">
                Loading Bluesky posts...
                {expanded && <LoaderCircle aria-hidden="true" className="size-4 motion-safe:animate-spin" />}
              </p>
            )}
            {current.status === "error" && (
              <div role="alert" className="space-y-2">
                <p>Unable to load post text from Bluesky. Recorded rankings are still shown.</p>
                <button type="button" className="relative z-20 rounded border px-3 py-1" onClick={() => setAttempt((value) => value + 1)}>
                  Try again
                </button>
              </div>
            )}
            <Table aria-label="Top Jerry parent posts by recorded replies">
              <TableHeader>
                <TableRow>
                  <TableHead scope="col" className="text-center align-middle">Rank</TableHead>
                  <TableHead scope="col">Post</TableHead>
                  <TableHead scope="col" className="text-right">Replies</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {posts.map((post, index) => (
                  <TableRow key={post.atUri}>
                    <TableHead scope="row" className="text-center align-middle tabular-nums">{index + 1}</TableHead>
                    <TableCell className="max-w-lg align-top whitespace-pre-wrap break-words">
                      <a href={blueskyPostUrl(post.atUri)} className="relative z-20 underline underline-offset-4">
                        {index < current.loaded
                          ? current.texts[post.atUri] === undefined
                            ? "Post unavailable"
                            : current.texts[post.atUri] || "Post has no text"
                          : current.status === "error" ? "Post text could not be loaded" : "View post on Bluesky"}
                      </a>
                    </TableCell>
                    <TableCell className="text-right align-middle tabular-nums">{post.replyCount.toLocaleString("en-US")}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </>
        )}
      </div>
  );
  if (expanded) return content;
  return (
    <Card>
      <CardHeader><CardTitle>Top 5 times Jerry needed to reconsider</CardTitle></CardHeader>
      <CardContent>
        <StatisticsDialog
          title="All times Jerry needed to reconsider"
          loadedTitle={(allPosts) => `All ${allPosts.length.toLocaleString("en-US")} times Jerry needed to reconsider`}
          triggerLabel="Show all Jerry posts by recorded replies"
          trigger={content}
          overlayTrigger
          path="/api/statistics/posts"
          isValid={isAllReplyPostList}
        >
          {(allPosts) => <TopReplyPosts posts={allPosts} expanded postCache={expandedCache} />}
        </StatisticsDialog>
      </CardContent>
    </Card>
  );
}
