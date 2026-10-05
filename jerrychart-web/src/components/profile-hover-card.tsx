"use client";

import Image from "next/image";
import { PreviewCard } from "@base-ui/react/preview-card";
import { useEffect, useRef, useState } from "react";
import { getBlueskyProfile, profileAvatarUrl, type BlueskyProfile } from "@/lib/bluesky-profile";
import type { TopReplyAuthor } from "@/lib/reply-authors";
import { ProfileText } from "@/components/profile-text";

export function ProfileHoverCard({ author, clickable = false }: {
  author: TopReplyAuthor;
  clickable?: boolean;
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
    if (!open) return;
    let active = true;
    void getBlueskyProfile(author.did).then(
      (result) => { if (active) setProfile(result); },
      (failure: unknown) => {
        console.error(`Unable to load Bluesky profile ${author.did}.`, failure);
        if (active) setFailedDid(author.did);
      },
    );
    return () => { active = false; };
  }, [open, author.did, attempt]);

  const avatar = profileAvatarUrl(profile?.avatar);
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
        className={`${clickable ? "relative z-20 " : ""}underline underline-offset-4`}
      >
        {author.handle ? `@${author.handle}` : author.did}
      </PreviewCard.Trigger>
      <PreviewCard.Portal container={container}>
        <PreviewCard.Positioner positionMethod="fixed" collisionBoundary={[]} sideOffset={8} className="z-50 max-w-[var(--available-width)]">
          <PreviewCard.Popup className="w-80 max-w-[calc(100vw_-_2rem)] rounded-xl border bg-background p-4 text-left text-sm font-normal text-foreground shadow-xl">
            {error ? (
              <div role="alert" className="space-y-2">
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
              <p role="status">Loading Bluesky profile...</p>
            ) : (
              <div className="space-y-3">
                <div className="flex items-center gap-3">
                  {avatar && (
                    <Image src={avatar} alt="" width={48} height={48} unoptimized className="size-12 shrink-0 rounded-full object-cover" />
                  )}
                  <div className="min-w-0 break-words">
                    <p className="font-semibold"><ProfileText text={profile.displayName || profile.handle} /></p>
                    <p className="text-muted-foreground">@{profile.handle}</p>
                  </div>
                </div>
                {profile.description && (
                  <p className="max-h-48 overflow-y-auto whitespace-pre-wrap break-words"><ProfileText text={profile.description} /></p>
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
