"use client";

import { useCallback, useEffect, useRef, useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";

const cooldownMilliseconds = 150_000;

export function RefreshStatistics() {
  const router = useRouter();
  const nextRefresh = useRef(0);
  const nextAutomaticRefresh = useRef(0);
  const [secondsRemaining, setSecondsRemaining] = useState(0);
  const [pending, startTransition] = useTransition();
  const refresh = useCallback(() => {
    const now = performance.now();
    if (pending || now < nextRefresh.current) return;
    nextRefresh.current = now + cooldownMilliseconds;
    nextAutomaticRefresh.current = nextRefresh.current;
    setSecondsRemaining(cooldownMilliseconds / 1000);
    startTransition(() => router.refresh());
  }, [pending, router]);

  useEffect(() => {
    if (nextAutomaticRefresh.current === 0) {
      nextAutomaticRefresh.current = performance.now() + cooldownMilliseconds;
    }
    function tick() {
      setSecondsRemaining(Math.max(0, Math.ceil((nextRefresh.current - performance.now()) / 1000)));
      if (document.visibilityState === "visible" && performance.now() >= nextAutomaticRefresh.current) {
        refresh();
      }
    }
    const timer = window.setInterval(tick, 1000);
    document.addEventListener("visibilitychange", tick);
    return () => {
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", tick);
    };
  }, [refresh]);

  return (
    <div className="flex justify-end">
      <Button
        type="button"
        variant="outline"
        disabled={pending || secondsRemaining > 0}
        onClick={refresh}
      >
        <span aria-hidden="true">⟳</span>{" "}
        {pending ? "Refreshing..." : secondsRemaining > 0
          ? `Refreshing in ${Math.floor(secondsRemaining / 60)}:${String(secondsRemaining % 60).padStart(2, "0")}`
          : "Refresh"}
      </Button>
    </div>
  );
}
