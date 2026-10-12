"use client";

import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { Maximize2, X } from "lucide-react";

interface StatisticsDialogProps<T> {
  title: string;
  loadedTitle?: (value: T) => string;
  triggerLabel: string;
  trigger: ReactNode;
  overlayTrigger?: boolean;
  scrollContent?: boolean;
  path: string;
  isValid: (value: unknown) => value is T;
  children: (value: T) => ReactNode;
}

export function StatisticsDialog<T>({
  title, loadedTitle, triggerLabel, trigger, overlayTrigger = false, scrollContent = true, path, isValid, children,
}: StatisticsDialogProps<T>) {
  const titleId = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  const pending = useRef<AbortController | null>(null);
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState(false);

  useEffect(() => () => pending.current?.abort(), []);

  async function load() {
    pending.current?.abort();
    const controller = new AbortController();
    pending.current = controller;
    setData(null);
    setError(false);
    try {
      const response = await fetch(path, {
        cache: "no-store",
        signal: AbortSignal.any([controller.signal, AbortSignal.timeout(15_000)]),
      });
      if (!response.ok) throw new Error(`Statistics request failed (HTTP ${response.status}).`);
      const result: unknown = await response.json();
      if (!isValid(result)) throw new Error("Invalid statistics response.");
      if (!controller.signal.aborted) setData(result);
    } catch (failure) {
      if (controller.signal.aborted) return;
      console.error(`Unable to load statistics from ${path}.`, failure);
      setError(true);
    }
  }

  const button = (
    <button
      type="button"
      className={`${overlayTrigger ? "absolute inset-0 z-10" : "block w-full text-left"} cursor-pointer rounded focus-visible:outline-2 focus-visible:outline-offset-4`}
      aria-label={triggerLabel}
      title={triggerLabel}
      aria-haspopup="dialog"
      onClick={() => {
        dialog.current?.showModal();
        void load();
      }}
    >
      {overlayTrigger ? (
        <span className="absolute bottom-0 right-0 flex items-center gap-1 text-xs underline underline-offset-4">
          View details <Maximize2 aria-hidden="true" className="size-4" />
        </span>
      ) : (
        <>
          {trigger}
          <span className="mt-3 flex items-center justify-end gap-1 text-xs underline underline-offset-4">
            View details <Maximize2 aria-hidden="true" className="size-4" />
          </span>
        </>
      )}
    </button>
  );

  return (
    <>
      {overlayTrigger ? <div className="relative pb-7">{trigger}{button}</div> : button}
      <dialog
        ref={dialog}
        aria-labelledby={titleId}
        className="fixed inset-0 m-auto max-h-[90vh] w-[calc(100%_-_2rem)] max-w-6xl overflow-hidden rounded-xl border bg-background p-6 text-foreground shadow-xl backdrop:bg-black/60"
        onClose={() => {
          pending.current?.abort();
          setData(null);
        }}
        onClick={(event) => {
          if (event.target === event.currentTarget) {
            const bounds = event.currentTarget.getBoundingClientRect();
            if (event.clientX < bounds.left || event.clientX > bounds.right ||
                event.clientY < bounds.top || event.clientY > bounds.bottom) dialog.current?.close();
          }
        }}
      >
        <div className="flex max-h-[calc(90vh_-_3rem)] flex-col">
          <div className="mb-6 flex shrink-0 items-start justify-between gap-4">
            <h2 id={titleId} className="text-xl font-semibold">{data !== null && loadedTitle ? loadedTitle(data) : title}</h2>
            <button
              type="button"
              aria-label="Close details"
              title="Close details"
              onClick={() => dialog.current?.close()}
              className="flex size-8 shrink-0 cursor-pointer items-center justify-center rounded border hover:bg-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-foreground"
            >
              <X aria-hidden="true" className="size-4" />
            </button>
          </div>
          <div data-statistics-scroll={scrollContent ? "" : undefined} className={scrollContent
            ? "min-h-0 overflow-x-hidden overflow-y-auto"
            : "flex min-h-0 flex-col"}>
            {error ? (
              <div role="alert" className="space-y-3">
                <p>Unable to load statistics. Please try again.</p>
                <button type="button" onClick={() => void load()} className="rounded border px-3 py-1">
                  Try again
                </button>
              </div>
            ) : data === null ? (
              <p role="status">Loading statistics...</p>
            ) : children(data)}
          </div>
        </div>
      </dialog>
    </>
  );
}
