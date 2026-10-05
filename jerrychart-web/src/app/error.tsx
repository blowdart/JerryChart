"use client";

import { useEffect } from "react";
import { Button } from "@/components/ui/button";

export default function ErrorPage({
  error,
  retry,
}: {
  error: Error & { digest?: string };
  retry: () => void;
}) {
  useEffect(() => {
    console.error("JerryChart could not complete the request.", error);
  }, [error]);

  return (
    <main className="mx-auto w-full max-w-4xl space-y-4 px-6 py-12">
      <h1 className="text-2xl font-semibold">Unable to load JerryChart data</h1>
      <p className="text-muted-foreground">
        Check the API and MySQL resources in the Aspire dashboard, then try again.
      </p>
      <Button onClick={() => retry()}>Try again</Button>
    </main>
  );
}
