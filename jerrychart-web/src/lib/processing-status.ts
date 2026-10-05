export type WorkerPhase = "not-started" | "archive" | "live" | "backfill-running" |
  "completed" | "stopped" | "failure" | "retrying";

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
  monitor: { activity: WorkerActivity; checkpointUpdatedAt: string | null; archiveEstimate: ArchiveReplayEstimate | null };
  parentUriBackfill: {
    activity: WorkerActivity;
    pending: number;
    retryPending: number;
    retryDue: number;
    resolved: number;
    unavailable: number;
  };
}

export interface ArchiveReplayEstimate {
  remainingSeconds: number;
  measuredAt: string;
}

// Match ArchiveReplayEstimate.MaximumAgeSeconds; heartbeat freshness remains independently limited to one minute.
const archiveEstimateMaximumAgeMilliseconds = 600_000;

const phases: WorkerPhase[] = [
  "not-started", "archive", "live", "backfill-running", "completed", "stopped", "failure", "retrying",
];
const activePhases: WorkerPhase[] = ["archive", "live", "backfill-running", "retrying"];
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

export function isProcessingStatus(value: unknown): value is ProcessingStatus {
  if (!object(value) || !utc(value.observedAt) || !object(value.monitor) ||
    !activity(value.monitor.activity) || !nullableUtc(value.monitor.checkpointUpdatedAt) ||
    !archiveEstimate(value.monitor.archiveEstimate) ||
    !object(value.parentUriBackfill) || !activity(value.parentUriBackfill.activity)) return false;
  const queue = value.parentUriBackfill;
  return ["pending", "retryPending", "retryDue", "resolved", "unavailable"].every((key) => count(queue[key])) &&
    (queue.retryDue as number) <= (queue.retryPending as number);
}

// Age the server's evaluation locally so a failed poll cannot leave a frozen "running" label.
export function activityMessage(
  activity: WorkerActivity, observedAt: string, elapsedMilliseconds: number, estimate?: ArchiveReplayEstimate | null,
): string {
  const expired = activePhases.includes(activity.phase) &&
    (activity.heartbeatAt === null || activity.state === "stale" ||
      Date.parse(observedAt) + Math.max(0, elapsedMilliseconds) - Date.parse(activity.heartbeatAt) >= 60_000);
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
