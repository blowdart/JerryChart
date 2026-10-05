"use client";

import { useEffect, useState } from "react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { getBlueskyPosts } from "@/lib/bluesky-posts";
import { blueskyPostUrl, type TopReplyPost } from "@/lib/reply-posts";

type PostState =
  | { list: string; status: "loading" | "error" }
  | { list: string; status: "ready"; texts: Record<string, string> };

export function TopReplyPosts({ posts }: { posts: TopReplyPost[] }) {
  const list = JSON.stringify(posts.map((post) => post.atUri));
  const [state, setState] = useState<PostState>({ list, status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setState({ list, status: "loading" });
    void getBlueskyPosts(JSON.parse(list) as string[], controller.signal).then(
      (texts) => {
        if (!controller.signal.aborted) setState({ list, status: "ready", texts });
      },
      (failure: unknown) => {
        if (controller.signal.aborted) return;
        console.error("Unable to load Jerry's Bluesky posts.", failure);
        setState({ list, status: "error" });
      },
    );
    return () => controller.abort();
  }, [list, attempt]);

  const current = state.list === list ? state : { status: "loading" as const };
  return (
    <Card>
      <CardHeader>
        <CardTitle>Top 5 times Jerry needed to reconsider</CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        {posts.length === 0 ? (
          <p className="text-sm text-muted-foreground">No recorded replies to resolved Jerry posts yet.</p>
        ) : (
          <>
            {current.status === "loading" && <p role="status">Loading Bluesky posts...</p>}
            {current.status === "error" && (
              <div role="alert" className="space-y-2">
                <p>Unable to load post text from Bluesky. Recorded rankings are still shown.</p>
                <button type="button" className="rounded border px-3 py-1" onClick={() => setAttempt((value) => value + 1)}>
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
                      <a href={blueskyPostUrl(post.atUri)} className="underline underline-offset-4">
                        {current.status === "ready"
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
      </CardContent>
    </Card>
  );
}
