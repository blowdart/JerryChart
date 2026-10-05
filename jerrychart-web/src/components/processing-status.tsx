"use client";

import { useEffect, useRef, useState } from "react";
import { activityMessage, isProcessingStatus, type ProcessingStatus as Status } from "@/lib/processing-status";

interface InitialStatus {
  status: Status | null;
  error: string | null;
}

function HeartbeatTime({ timestamp }: { timestamp: string | null }) {
  const [formatter, setFormatter] = useState<Intl.DateTimeFormat | null>(null);

  useEffect(() => {
    setFormatter(new Intl.DateTimeFormat(navigator.languages.length ? [...navigator.languages] : undefined, {
      dateStyle: "medium",
      timeStyle: "short",
    }));
  }, []);

  if (timestamp === null) return <>not recorded</>;
  return (
    <time dateTime={timestamp}>
      {formatter ? formatter.format(new Date(timestamp)) : `${timestamp} (UTC)`}
    </time>
  );
}

export function ProcessingStatus({ initial }: { initial: InitialStatus }) {
  const [snapshot, setSnapshot] = useState(initial.status);
  const [error, setError] = useState(initial.error);
  const [elapsed, setElapsed] = useState(0);
  const receivedAt = useRef<number | null>(null);

  useEffect(() => {
    let disposed = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let request: AbortController | undefined;
    let refreshRequested = false;
    if (initial.status || receivedAt.current === null) receivedAt.current = performance.now();
    if (initial.status) setSnapshot(initial.status);
    setError(initial.error);
    setElapsed(performance.now() - receivedAt.current);

    function schedule() {
      if (!disposed && document.visibilityState === "visible") {
        timer = setTimeout(refresh, 30_000);
      }
    }

    async function refresh() {
      if (disposed || document.visibilityState !== "visible" || request) return;
      setElapsed(performance.now() - receivedAt.current!);
      const controller = new AbortController();
      request = controller;
      try {
        const response = await fetch("/api/statistics/processing-status", {
          cache: "no-store",
          signal: AbortSignal.any([controller.signal, AbortSignal.timeout(10_000)]),
        });
        if (!response.ok) throw new Error(`Processing status returned HTTP ${response.status}.`);
        const status: unknown = await response.json();
        if (!isProcessingStatus(status)) throw new Error("Invalid processing status response.");
        if (disposed || controller.signal.aborted) return;
        receivedAt.current = performance.now();
        setSnapshot(status);
        setElapsed(0);
        setError(null);
      } catch (failure) {
        if (!disposed && !controller.signal.aborted) {
          console.error("Unable to refresh processing status.", failure);
          setElapsed(performance.now() - receivedAt.current!);
          setError("Unable to refresh processing status. Last known status shown; activity may be stale.");
        }
      } finally {
        if (request === controller) {
          request = undefined;
          if (refreshRequested && !disposed && document.visibilityState === "visible") {
            refreshRequested = false;
            void refresh();
          } else {
            schedule();
          }
        }
      }
    }

    function visibilityChanged() {
      clearTimeout(timer);
      if (document.visibilityState !== "visible") {
        request?.abort();
        return;
      }
      setElapsed(performance.now() - receivedAt.current!);
      if (request) {
        request.abort();
        refreshRequested = true;
        return;
      }
      void refresh();
    }

    document.addEventListener("visibilitychange", visibilityChanged);
    if (initial.error) void refresh();
    else schedule();
    return () => {
      disposed = true;
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", visibilityChanged);
      request?.abort();
    };
  }, [initial]);

  const queue = snapshot?.parentUriBackfill;
  const showParentUris = queue !== undefined && queue.pending + queue.retryPending > 0;
  const format = (count: number) => count.toLocaleString("en-US");
  return (
    <>
      <p>
        <span aria-label="Heartbeat">♥</span>{" "}: Jetstream <HeartbeatTime timestamp={snapshot?.monitor.activity.heartbeatAt ?? null} />
        {showParentUris && queue && <>; parent URIs <HeartbeatTime timestamp={queue.activity.heartbeatAt} /></>}
      </p>
    <section aria-label="Processing status" className="space-y-2 rounded-lg border p-4 text-sm text-muted-foreground">
      <h2 className="font-medium text-foreground">Processing status</h2>
      {error && <p role="alert">{error}</p>}
      {snapshot && queue ? (
        <>
          <p><strong>Jetstream:</strong>{" "}
            {activityMessage(snapshot.monitor.activity, snapshot.observedAt, elapsed, snapshot.monitor.archiveEstimate)}
          </p>
          {showParentUris && <>
          <p><strong>Parent URIs:</strong>{" "}
            {activityMessage(queue.activity, snapshot.observedAt, elapsed)}
          </p>
          <p>
            {queue.pending + queue.retryPending === 0 ? "No outstanding parent-URI rows." :
              `${format(queue.pending + queue.retryPending)} parent-URI rows remaining.`}{" "}
            {format(queue.pending)} pending; {format(queue.retryPending)} retryable ({format(queue.retryDue)} due);{" "}
            {format(queue.resolved)} resolved; {format(queue.unavailable)} unavailable (not retried).
          </p>
          </>}
          <p className="text-xs">
            Status refreshes every 30 seconds while this page is visible; statistics refresh separately.
          </p>
        </>
      ) : !error && <p>Worker activity is unknown.</p>}
    </section>
    </>
  );
}
