"use client";

import { useEffect, useId, useRef, useState, type ReactNode } from "react";

interface StatisticsDialogProps<T> {
  title: string;
  triggerLabel: string;
  trigger: ReactNode;
  overlayTrigger?: boolean;
  path: string;
  isValid: (value: unknown) => value is T;
  children: (value: T) => ReactNode;
}

export function StatisticsDialog<T>({
  title, triggerLabel, trigger, overlayTrigger = false, path, isValid, children,
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
      aria-haspopup="dialog"
      onClick={() => {
        dialog.current?.showModal();
        void load();
      }}
    >
      {overlayTrigger ? null : trigger}
    </button>
  );

  return (
    <>
      {overlayTrigger ? <div className="relative">{trigger}{button}</div> : button}
      <dialog
        ref={dialog}
        aria-labelledby={titleId}
        className="fixed inset-0 m-auto max-h-[90vh] w-[calc(100%_-_2rem)] max-w-6xl overflow-hidden rounded-xl border bg-background p-6 text-foreground shadow-xl backdrop:bg-black/60"
        onClose={() => pending.current?.abort()}
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
            <h2 id={titleId} className="text-xl font-semibold">{title}</h2>
            <button type="button" onClick={() => dialog.current?.close()} className="rounded border px-3 py-1">
              Close
            </button>
          </div>
          <div data-statistics-scroll className="min-h-0 overflow-x-hidden overflow-y-auto">
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
