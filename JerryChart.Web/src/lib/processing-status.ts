export type WorkerPhase = "not-started" | "archive" | "live" | "backfill-running" |
  "completed" | "stopped" | "failure" | "retrying" |
  "handle-refresh-idle" | "handle-refresh-running" | "handle-refresh-waiting";

export interface WorkerActivity {
  phase: WorkerPhase;
  state: WorkerPhase | "stale";
  isRunning: boolean;
  startedAt: string | null;
  changedAt: string | null;
  heartbeatAt: string | null;
  finishedAt: string | null;
}

export interface ProcessingStatus {
  observedAt: string;
  monitor: {
    activity: WorkerActivity;
    checkpointUpdatedAt: string | null;
    archiveEstimate: ArchiveReplayEstimate | null;
    archiveReplay?: ArchiveReplayStatus | null;
    archiveThroughput?: ArchiveReplayThroughput | null;
  };
  parentUriBackfill: {
    activity: WorkerActivity;
    pending: number;
    retryPending: number;
    retryDue: number;
    resolved: number;
    unavailable: number;
  };
  handleRefresh?: {
    activity: WorkerActivity;
    pending: number;
    due: number;
    nextDueAt: string | null;
  };
}

export interface ArchiveReplayEstimate {
  remainingSeconds: number;
  measuredAt: string;
}

export interface ArchiveReplayThroughput {
  deliveredEvents: number;
  windowSeconds: number;
  windowStartedAt: string;
  measuredAt: string;
  eventsPerSecond: number;
  microsecondsPerEvent: number;
}

export interface ArchiveReplayStatus {
  noProgressSince: string;
  lastProgressAt: string | null;
  stalledSince: string | null;
  consecutiveGenerationMismatches: number;
  nextRetryAt: string | null;
}

// Match ArchiveReplayEstimate.MaximumAgeSeconds; heartbeat freshness remains independently limited to one minute.
const archiveEstimateMaximumAgeMilliseconds = 600_000;

const phases: WorkerPhase[] = [
  "not-started", "archive", "live", "backfill-running", "completed", "stopped", "failure", "retrying",
  "handle-refresh-idle", "handle-refresh-running", "handle-refresh-waiting",
];
const activePhases: WorkerPhase[] = ["archive", "live", "backfill-running", "retrying",
  "handle-refresh-idle", "handle-refresh-running", "handle-refresh-waiting"];
const object = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null;
const utc = (value: unknown): value is string =>
  typeof value === "string" && /(?:Z|\+00:00)$/.test(value) && Number.isFinite(Date.parse(value));
const nullableUtc = (value: unknown) => value === null || utc(value);
const count = (value: unknown): value is number =>
  typeof value === "number" && Number.isSafeInteger(value) && value >= 0;

function activity(value: unknown): value is WorkerActivity {
  return object(value) && phases.includes(value.phase as WorkerPhase) &&
    (value.state === value.phase || (value.state === "stale" && activePhases.includes(value.phase as WorkerPhase))) &&
    typeof value.isRunning === "boolean" &&
    value.isRunning === (activePhases.includes(value.phase as WorkerPhase) && value.state !== "stale") &&
    ["startedAt", "changedAt", "heartbeatAt", "finishedAt"].every((key) => nullableUtc(value[key])) &&
    (value.phase === "not-started"
      ? [value.startedAt, value.changedAt, value.heartbeatAt, value.finishedAt].every((time) => time === null)
      : utc(value.startedAt) && utc(value.changedAt) && utc(value.heartbeatAt));
}

function archiveEstimate(value: unknown): value is ArchiveReplayEstimate | null {
  return value === null || (object(value) && typeof value.remainingSeconds === "number" &&
    Number.isFinite(value.remainingSeconds) && value.remainingSeconds >= 0 && utc(value.measuredAt));
}

function archiveReplay(value: unknown): value is ArchiveReplayStatus | null | undefined {
  return value === undefined || value === null || (object(value) && utc(value.noProgressSince) &&
    nullableUtc(value.lastProgressAt) && nullableUtc(value.stalledSince) && nullableUtc(value.nextRetryAt) &&
    count(value.consecutiveGenerationMismatches));
}

function archiveThroughput(value: unknown): value is ArchiveReplayThroughput | null | undefined {
  if (value === undefined || value === null) return true;
  if (!object(value) || !count(value.deliveredEvents) || value.deliveredEvents === 0 ||
    typeof value.windowSeconds !== "number" || !Number.isFinite(value.windowSeconds) || value.windowSeconds < 120 ||
    !utc(value.windowStartedAt) || !utc(value.measuredAt) ||
    Date.parse(value.windowStartedAt) > Date.parse(value.measuredAt)) return false;
  // Derived rates must agree with the measured count and elapsed window, not sequence distance.
  const matches = (actual: unknown, expected: number) =>
    typeof actual === "number" && Number.isFinite(actual) && actual > 0 &&
    Math.abs(actual - expected) <= expected * 1e-12;
  return matches(value.eventsPerSecond, value.deliveredEvents / value.windowSeconds) &&
    matches(value.microsecondsPerEvent, value.windowSeconds * 1_000_000 / value.deliveredEvents);
}

export function isProcessingStatus(value: unknown): value is ProcessingStatus {
  if (!object(value) || !utc(value.observedAt) || !object(value.monitor) ||
    !activity(value.monitor.activity) || !nullableUtc(value.monitor.checkpointUpdatedAt) ||
    !archiveEstimate(value.monitor.archiveEstimate) ||
    !archiveReplay(value.monitor.archiveReplay) ||
    !archiveThroughput(value.monitor.archiveThroughput) ||
    !object(value.parentUriBackfill) || !activity(value.parentUriBackfill.activity)) return false;
  const queue = value.parentUriBackfill;
  const handles = value.handleRefresh;
  // Accept older API snapshots during rolling deployment without inferring handle activity.
  if (handles !== undefined && (!object(handles) || !activity(handles.activity) ||
    !count(handles.pending) || !count(handles.due) || handles.due > handles.pending ||
    !nullableUtc(handles.nextDueAt) || (handles.pending === 0) !== (handles.nextDueAt === null))) return false;
  return ["pending", "retryPending", "retryDue", "resolved", "unavailable"].every((key) => count(queue[key])) &&
    (queue.retryDue as number) <= (queue.retryPending as number);
}

function heartbeatExpired(activity: WorkerActivity, observedAt: string, elapsedMilliseconds: number): boolean {
  return activePhases.includes(activity.phase) &&
    (activity.heartbeatAt === null || activity.state === "stale" ||
      Date.parse(observedAt) + Math.max(0, elapsedMilliseconds) - Date.parse(activity.heartbeatAt) >= 60_000);
}

export function replayThroughputMessage(
  monitor: ProcessingStatus["monitor"], observedAt: string, elapsedMilliseconds: number,
): string | null {
  const measurement = monitor.archiveThroughput;
  const activity = monitor.activity;
  const now = Date.parse(observedAt) + Math.max(0, elapsedMilliseconds);
  if (!measurement || !archiveThroughput(measurement) || monitor.archiveReplay?.stalledSince ||
    activity.phase !== "archive" || activity.state !== "archive" || !activity.isRunning ||
    heartbeatExpired(activity, observedAt, elapsedMilliseconds) ||
    Date.parse(measurement.measuredAt) > now || now - Date.parse(measurement.measuredAt) >= 60_000 ||
    Date.parse(measurement.windowStartedAt) < Date.parse(activity.changedAt!) ||
    Date.parse(measurement.windowStartedAt) < Date.parse(activity.startedAt!)) return null;
  const format = (value: number) => value.toLocaleString("en-US", { maximumSignificantDigits: 3 });
  return `Replay throughput (window average): ${format(measurement.eventsPerSecond)} events/s ` +
    `(${format(measurement.microsecondsPerEvent)} µs/event).`;
}

export function activitySummary(
  activity: WorkerActivity, observedAt: string, elapsedMilliseconds: number, replay?: ArchiveReplayStatus | null,
): string {
  if (heartbeatExpired(activity, observedAt, elapsedMilliseconds)) return "Heartbeat stale; activity unknown.";
  if (replay?.stalledSince) return "Archive stalled.";
  switch (activity.state) {
    case "archive": return "Historical replay in progress.";
    case "live": return "Listening to live events.";
    case "backfill-running": return "Parent-URI backfill in progress.";
    case "handle-refresh-idle": return "Handle updater is idle; waiting for due refreshes.";
    case "handle-refresh-running": return "Refreshing actor handles.";
    case "handle-refresh-waiting": return "Waiting for request pacing or a server rate limit.";
    case "retrying": return "Retrying after a temporary failure or waiting for scheduled retries.";
    case "completed": return "Last recorded run completed.";
    case "stopped": return "Stopped; saved work can resume on the next run.";
    case "failure": return "Last run failed. See worker logs for details.";
    case "not-started": return "Not started / unknown: no worker run has been recorded.";
    case "stale": return "Heartbeat stale; activity unknown.";
  }
}

export function activityOutcome(activity: WorkerActivity, observedAt: string, elapsedMilliseconds: number): string {
  if (activity.phase === "not-started") return "Never run.";
  if (heartbeatExpired(activity, observedAt, elapsedMilliseconds)) {
    return `Activity stale; the run may have been interrupted (last phase: ${activity.phase}).`;
  }
  if (["completed", "stopped", "failure"].includes(activity.phase) && activity.finishedAt === null) {
    return `No finish recorded (last phase: ${activity.phase}).`;
  }
  switch (activity.state) {
    case "archive": return "In progress (historical replay).";
    case "live": return "In progress (live monitoring).";
    case "backfill-running": return "In progress.";
    case "handle-refresh-idle": return "Active (idle; no due refreshes found).";
    case "handle-refresh-running": return "In progress (handle refresh).";
    case "handle-refresh-waiting": return "Active (request pacing / rate-limit wait).";
    case "retrying": return "Retrying.";
    case "completed": return "Completed.";
    case "stopped": return "Stopped before completion.";
    case "failure": return "Failed.";
    case "not-started": return "Never run.";
    case "stale": return `Activity stale; the run may have been interrupted (last phase: ${activity.phase}).`;
  }
}

// Age the server's evaluation locally so a failed poll cannot leave a frozen "running" label.
export function activityMessage(
  activity: WorkerActivity, observedAt: string, elapsedMilliseconds: number, estimate?: ArchiveReplayEstimate | null,
  replay?: ArchiveReplayStatus | null,
): string {
  const expired = heartbeatExpired(activity, observedAt, elapsedMilliseconds);
  if (replay?.stalledSince) {
    const now = Date.parse(observedAt) + Math.max(0, elapsedMilliseconds);
    const duration = remainingDuration(Math.max(0, (now - Date.parse(replay.stalledSince)) / 1000));
    const noProgress = remainingDuration(Math.max(0, (now - Date.parse(replay.noProgressSince)) / 1000));
    const heartbeat = expired ? `Heartbeat stale; activity unknown (last phase: ${activity.phase}). ` :
      activity.isRunning ? "Worker heartbeat is fresh, but archive processing is not progressing. " :
        `Worker ${activity.state}. `;
    return `${heartbeat}${expired || !activity.isRunning ? "Last recorded archive stall" : "Archive stalled"}: ` +
      `${duration} stalled; ${noProgress} without successful processing or durable progress; ` +
      `${replay.consecutiveGenerationMismatches.toLocaleString("en-US")} consecutive generation mismatches. ` +
      "Retries and durable checkpoint recovery are unchanged.";
  }
  if (expired) return `Heartbeat stale; activity unknown (last phase: ${activity.phase}).`;
  switch (activity.state) {
    case "archive": {
      const now = Date.parse(observedAt) + Math.max(0, elapsedMilliseconds);
      const measured = estimate ? Date.parse(estimate.measuredAt) : NaN;
      if (!estimate || measured > now || now - measured >= archiveEstimateMaximumAgeMilliseconds ||
        measured < Date.parse(activity.changedAt!) || measured < Date.parse(activity.startedAt!)) {
        return "Historical replay in progress.";
      }
      return `Historical replay in progress. Approximate time remaining: ${remainingDuration(estimate.remainingSeconds)}.`;
    }
    case "live": return "Listening to live events. Historical replay has reached live monitoring.";
    case "backfill-running": return "Parent-URI backfill in progress.";
    case "handle-refresh-idle": return "Handle updater is idle; waiting for due refreshes.";
    case "handle-refresh-running": return "Refreshing actor handles.";
    case "handle-refresh-waiting": return "Waiting for request pacing or a server rate limit.";
    case "retrying": return "Retrying after a temporary failure or waiting for scheduled retries.";
    case "completed": return "Last recorded run completed.";
    case "stopped": return "Stopped; saved work can resume on the next run.";
    case "failure": return "Last run failed. See worker logs for details.";
    case "not-started": return "Not started / unknown: no worker run has been recorded.";
    case "stale": return "Heartbeat stale; activity unknown.";
  }
}

function remainingDuration(seconds: number): string {
  if (seconds < 60) return "less than a minute";
  const minutes = Math.ceil(seconds / 60);
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const remainder = minutes % 60;
  return [
    days ? `${days.toLocaleString("en-US")} ${days === 1 ? "day" : "days"}` : "",
    hours ? `${hours} ${hours === 1 ? "hour" : "hours"}` : "",
    remainder ? `${remainder} ${remainder === 1 ? "minute" : "minutes"}` : "",
  ].filter(Boolean).join(" ");
}
