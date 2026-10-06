"use client";

import Image from "next/image";
import { PreviewCard } from "@base-ui/react/preview-card";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { getBlueskyProfile, profileAvatarUrl, type BlueskyProfile } from "@/lib/bluesky-profile";
import { authorLabel, type TopReplyAuthor } from "@/lib/reply-authors";
import { ProfileText } from "@/components/profile-text";
import { profileDisplayText } from "@/lib/profile-display-text";

export function ProfileHoverCard({ author, clickable = false, children, className }: {
  author: Pick<TopReplyAuthor, "did" | "handle" | "accountStatus">;
  clickable?: boolean;
  children?: ReactNode;
  className?: string;
}) {
  const trigger = useRef<HTMLAnchorElement>(null);
  const [container, setContainer] = useState<HTMLElement | null>(null);
  const [open, setOpen] = useState(false);
  const [savedProfile, setProfile] = useState<BlueskyProfile | null>(null);
  const [failedDid, setFailedDid] = useState<string | null>(null);
  const profile = savedProfile?.did === author.did ? savedProfile : null;
  const error = failedDid === author.did;
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!open || author.handle === null) return;
    let active = true;
    void getBlueskyProfile(author.did).then(
      (result) => { if (active) setProfile(result); },
      (failure: unknown) => {
        console.error(`Unable to load Bluesky profile ${author.did}.`, failure);
        if (active) setFailedDid(author.did);
      },
    );
    return () => { active = false; };
  }, [open, author.did, author.handle, attempt]);

  const avatar = profileAvatarUrl(profile?.avatar);
  const description = profile ? profileDisplayText(profile.description ?? "").trimStart() : "";
  const displayName = profile ? profileDisplayText(profile.displayName ?? "").trim() || profile.handle : "";
  // Status labels are not handles: avoid @ prefixes, dead profile links, and futile hover requests.
  if (author.handle === null) {
    return <span title={author.did} className={className}>{children ?? authorLabel(author)}</span>;
  }
  return (
    <PreviewCard.Root
      open={open}
      onOpenChange={(next) => {
        if (next) {
          setContainer(trigger.current?.closest("dialog") ?? document.body);
          setProfile(null);
          setFailedDid(null);
        }
        setOpen(next);
      }}
    >
      <PreviewCard.Trigger
        ref={trigger}
        href={`https://bsky.app/profile/${author.did}`}
        className={`${clickable ? "relative z-20 " : ""}${className ?? "underline underline-offset-4"}`}
      >
        {children ?? authorLabel(author)}
      </PreviewCard.Trigger>
      <PreviewCard.Portal container={container}>
        <PreviewCard.Positioner positionMethod="fixed" collisionBoundary={[]} sideOffset={8} className="z-50 max-w-[var(--available-width)]">
          <PreviewCard.Popup className="relative w-80 max-w-[calc(100vw_-_2rem)] rounded-xl border bg-background p-4 text-left text-sm font-normal text-foreground shadow-xl">
            <button
              type="button"
              aria-label="Close profile details"
              className="absolute right-2 top-2 flex size-8 items-center justify-center rounded hover:bg-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-foreground"
              onClick={() => {
                setOpen(false);
                trigger.current?.focus();
              }}
            >
              <svg aria-hidden="true" width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M4 4l8 8M12 4l-8 8" />
              </svg>
            </button>
            {error ? (
              <div role="alert" className="space-y-2 pr-8">
                <p>Unable to load this Bluesky profile.</p>
                <button type="button" className="rounded border px-3 py-1" onClick={() => {
                  setProfile(null);
                  setFailedDid(null);
                  setAttempt((value) => value + 1);
                }}>
                  Try again
                </button>
              </div>
            ) : profile === null ? (
              <p role="status" className="pr-8">Loading Bluesky profile...</p>
            ) : (
              <div className="space-y-3">
                <div className="flex items-center gap-3 pr-8">
                  {avatar && (
                    <Image src={avatar} alt="" width={48} height={48} unoptimized className="size-12 shrink-0 rounded-full object-cover" />
                  )}
                  <div className="min-w-0 flex-1">
                    <p className="truncate font-semibold" dir="auto" title={displayName}>{displayName}</p>
                    <p className="truncate text-muted-foreground" dir="ltr" title={`@${profile.handle}`}>@{profile.handle}</p>
                  </div>
                </div>
                {description && (
                  <p className="max-h-48 overflow-y-auto whitespace-pre-wrap break-words"><ProfileText text={description} /></p>
                )}
                <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
                  {profile.followersCount !== undefined && <span>{profile.followersCount.toLocaleString()} followers</span>}
                  {profile.followsCount !== undefined && <span>{profile.followsCount.toLocaleString()} following</span>}
                  {profile.postsCount !== undefined && <span>{profile.postsCount.toLocaleString()} posts</span>}
                </div>
              </div>
            )}
          </PreviewCard.Popup>
        </PreviewCard.Positioner>
      </PreviewCard.Portal>
    </PreviewCard.Root>
  );
}
