"use client";

import { useEffect, useId, useRef, useState } from "react";
import { activityMessage, activityOutcome, activitySummary, isProcessingStatus, type ProcessingStatus as Status } from "@/lib/processing-status";
import { LocalizedTime } from "@/components/localized-time";

interface InitialStatus {
  status: Status | null;
  error: string | null;
}

export function ProcessingStatus({ initial }: { initial: InitialStatus }) {
  const [snapshot, setSnapshot] = useState(initial.status);
  const [error, setError] = useState(initial.error);
  const [elapsed, setElapsed] = useState(0);
  const receivedAt = useRef<number | null>(null);
  const detailsTitleId = useId();
  const detailsDialog = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    let disposed = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let request: AbortController | undefined;
    let refreshRequested = false;
    if (initial.status || receivedAt.current === null) receivedAt.current = performance.now();
    // Keep cached data on failed router refreshes; synchronize successful server snapshots with the polling lifecycle.
    // eslint-disable-next-line react-hooks/set-state-in-effect
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
  const handles = snapshot?.handleRefresh;
  const showParentUris = queue !== undefined && queue.pending + queue.retryPending > 0;
  const format = (count: number) => count.toLocaleString("en-US");
  return (
    <>
      {error && <p role="alert">{error}</p>}
      <button
        type="button"
        aria-haspopup="dialog"
        aria-label="Processing status details"
        className="ml-auto block w-fit max-w-full rounded-lg text-right focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-foreground"
        onClick={() => detailsDialog.current?.showModal()}
      >
        <span className="block space-y-2 rounded-lg border p-4 text-sm text-muted-foreground">
          <span className="block font-medium text-foreground">Processing status</span>
          {snapshot
            ? <span className="block"><strong>Jetstream:</strong>{" "}
              {activitySummary(snapshot.monitor.activity, snapshot.observedAt, elapsed, snapshot.monitor.archiveReplay)}
            </span>
            : !error && <span className="block">Worker activity is unknown.</span>}
          <span className="block text-xs underline underline-offset-4">View detailed status</span>
        </span>
      </button>
      <dialog
        ref={detailsDialog}
        aria-labelledby={detailsTitleId}
        className="fixed inset-0 m-auto max-h-[90vh] w-[calc(100%_-_2rem)] max-w-2xl overflow-auto rounded-xl border bg-background p-6 text-left text-foreground shadow-xl backdrop:bg-black/60"
      >
        <div className="mb-6 flex items-start justify-between gap-4">
          <h2 id={detailsTitleId} className="text-xl font-semibold">Processing status details</h2>
          <button
            type="button"
            autoFocus
            onClick={() => detailsDialog.current?.close()}
            className="rounded border px-3 py-1 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-foreground"
          >
            Close
          </button>
        </div>
        <div className="space-y-2 text-sm text-muted-foreground">
          {error && <p role="alert">{error}</p>}
          {snapshot && queue ? (
            <>
              <h3 className="pt-2 font-medium text-foreground">Jetstream processing</h3>
              <p>
                {activityMessage(snapshot.monitor.activity, snapshot.observedAt, elapsed,
                  snapshot.monitor.archiveEstimate, snapshot.monitor.archiveReplay)}
              </p>
              {snapshot.monitor.archiveReplay && <>
                <p>Last successful processing / durable progress:{" "}
                  <LocalizedTime timestamp={snapshot.monitor.archiveReplay.lastProgressAt} />.
                </p>
                {(snapshot.monitor.archiveReplay.stalledSince ||
                  snapshot.monitor.archiveReplay.consecutiveGenerationMismatches > 0) && <p>
                  Consecutive archive generation mismatches:{" "}
                  {format(snapshot.monitor.archiveReplay.consecutiveGenerationMismatches)}. Next scheduled retry:{" "}
                  {snapshot.monitor.archiveReplay.nextRetryAt
                    ? <LocalizedTime timestamp={snapshot.monitor.archiveReplay.nextRetryAt} />
                    : "none recorded (an attempt may be underway)"}.
                </p>}
              </>}
            </>
          ) : !error && <p>Worker activity is unknown.</p>}
          <p>
            Jetstream heartbeat: <LocalizedTime timestamp={snapshot?.monitor.activity.heartbeatAt ?? null} />
          </p>
          {queue && <>
            <h3 className="pt-2 font-medium text-foreground">Parent-URI backfill</h3>
            <p>{activityOutcome(queue.activity, snapshot?.observedAt ?? "", elapsed)}</p>
            <p>Activity started:{" "}
              {queue.activity.startedAt
                ? <LocalizedTime timestamp={queue.activity.startedAt} />
                : "not recorded (never run)."}
            </p>
            <p>Activity finished:{" "}
              {queue.activity.finishedAt
                ? <LocalizedTime timestamp={queue.activity.finishedAt} />
                : queue.activity.phase === "not-started"
                  ? "not recorded (never run)."
                  : queue.activity.isRunning
                    ? "not recorded (still active)."
                    : "not recorded (no completion time is available)."}
            </p>
            {queue.activity.heartbeatAt && <p>
              Parent-URI heartbeat: <LocalizedTime timestamp={queue.activity.heartbeatAt} />
            </p>}
            {showParentUris && snapshot && <>
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
          </>}
          <h3 className="pt-2 font-medium text-foreground">Handle refresh</h3>
          {handles && snapshot ? <>
            <p>{activityOutcome(handles.activity, snapshot.observedAt, elapsed)}</p>
            <p>{activityMessage(handles.activity, snapshot.observedAt, elapsed)}</p>
            <p>Activity started:{" "}
              {handles.activity.startedAt
                ? <LocalizedTime timestamp={handles.activity.startedAt} />
                : "not recorded (never run)."}
            </p>
            <p>Last phase change: <LocalizedTime timestamp={handles.activity.changedAt} /></p>
            {handles.activity.finishedAt && <p>Activity finished:{" "}
              <LocalizedTime timestamp={handles.activity.finishedAt} />
            </p>}
            <p>Handle refresh heartbeat: <LocalizedTime timestamp={handles.activity.heartbeatAt} /></p>
            <p>
              {format(handles.pending)} refreshes scheduled ({format(handles.due)} due).{" "}
              Earliest queued eligibility:{" "}
              {handles.nextDueAt ? <LocalizedTime timestamp={handles.nextDueAt} /> : "none (empty queue)"}.
            </p>
          </> : <p>Handle refresh activity is unknown; no status snapshot is available.</p>}
          <p className="text-xs">
            Status refreshes every 30 seconds while this page is visible; statistics refresh separately.
          </p>
        </div>
      </dialog>
    </>
  );
}
