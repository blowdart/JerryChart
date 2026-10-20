"use client";

import { Popover } from "@base-ui/react/popover";
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { X } from "lucide-react";
import { blueskyPostUrl } from "@/lib/reply-posts";

export function PostHoverCard({ atUri, children }: { atUri: string; children: ReactNode }) {
  const href = blueskyPostUrl(atUri);
  const id = useId();
  const trigger = useRef<HTMLAnchorElement>(null);
  const frame = useRef<HTMLIFrameElement>(null);
  const loadTimeout = useRef<number | undefined>(undefined);
  const suppressFocusOpen = useRef(false);
  const [open, setOpen] = useState(false);
  const [height, setHeight] = useState(400);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (!open) return;
    function resize(event: MessageEvent<unknown>) {
      if (event.origin !== "https://embed.bsky.app" || !frame.current ||
        event.source !== frame.current.contentWindow) return;
      const data = event.data;
      if (typeof data !== "object" || data === null ||
        !("id" in data) || data.id !== id ||
        !("height" in data) || typeof data.height !== "number" ||
        !Number.isFinite(data.height) || data.height <= 0 || data.height > 10000) return;
      setHeight(Math.ceil(data.height));
      window.clearTimeout(loadTimeout.current);
      setLoading(false);
      setFailed(false);
    }
    window.addEventListener("message", resize);
    loadTimeout.current = window.setTimeout(() => {
      setLoading(false);
      setFailed(true);
    }, 15000);
    return () => {
      window.removeEventListener("message", resize);
      window.clearTimeout(loadTimeout.current);
    };
  }, [open, id]);

  return (
    <Popover.Root open={open} triggerId={id} onOpenChange={(next, details) => {
      if (details.reason === "trigger-press") {
        details.cancel();
        return;
      }
      if (next) {
        setHeight(400);
        setLoading(true);
        setFailed(false);
      } else if (details.reason === "escape-key" || details.reason === "close-press") {
        suppressFocusOpen.current = true;
      }
      setOpen(next);
    }}>
      <Popover.Trigger id={id} nativeButton={false} openOnHover
        render={<a ref={trigger} href={href} />} role="link" className="underline underline-offset-4"
        onClick={(event) => event.preventBaseUIHandler()}
        onKeyDown={(event) => {
          if (event.key === " ") event.preventBaseUIHandler();
        }}
        onFocus={() => {
          if (!open && !suppressFocusOpen.current) {
            setHeight(400);
            setLoading(true);
            setFailed(false);
            setOpen(true);
          }
        }}
        onBlur={() => { suppressFocusOpen.current = false; }}>
        {children}
      </Popover.Trigger>
      {open && (
        <Popover.Portal>
          <Popover.Positioner sideOffset={8} positionMethod="fixed" className="z-50 max-w-[var(--available-width)]">
            <Popover.Popup initialFocus={false} aria-label="Matching reply preview" className="flex max-h-[var(--available-height)] w-[400px] max-w-[calc(100vw_-_2rem)] flex-col rounded-xl border bg-background p-3 text-left text-sm font-normal text-foreground shadow-xl">
              <div className="mb-2 flex shrink-0 items-center justify-between gap-3">
                <span>Bluesky reply preview</span>
                <button type="button" aria-label="Close reply preview" title="Close reply preview"
                  className="flex size-8 shrink-0 cursor-pointer items-center justify-center rounded border hover:bg-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-foreground"
                  onClick={() => {
                    suppressFocusOpen.current = true;
                    setOpen(false);
                    trigger.current?.focus();
                  }}>
                  <X aria-hidden="true" className="size-4" />
                </button>
              </div>
              {loading && <p role="status" className="shrink-0">Loading Bluesky preview...</p>}
              {failed && <p role="alert" className="shrink-0">Unable to load the Bluesky preview. Use the link below to view the reply.</p>}
              <div role="region" aria-label="Bluesky post. Scroll to see the full preview." tabIndex={0}
                className="min-h-0 max-h-[60vh] overflow-auto focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-foreground">
                <iframe ref={frame}
                  src={`https://embed.bsky.app/embed/${atUri.slice("at://".length)}?id=${encodeURIComponent(id)}&colorMode=system`}
                  title="Matching reply on Bluesky"
                  className="block w-full min-w-[300px] border-0"
                  height={height}
                  referrerPolicy="no-referrer"
                  sandbox="allow-scripts allow-same-origin allow-popups allow-popups-to-escape-sandbox"
                  onError={() => {
                    window.clearTimeout(loadTimeout.current);
                    setLoading(false);
                    setFailed(true);
                  }}
                />
              </div>
              <a href={href} className="mt-2 inline-block shrink-0 underline underline-offset-4">View the reply on Bluesky</a>
            </Popover.Popup>
          </Popover.Positioner>
        </Popover.Portal>
      )}
    </Popover.Root>
  );
}
