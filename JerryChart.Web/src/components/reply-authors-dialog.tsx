"use client";

import { ReplyAuthorsTable } from "@/components/reply-authors-table";
import { StatisticsDialog } from "@/components/statistics-dialog";
import { isReplyAuthorList, type TopReplyAuthor } from "@/lib/reply-authors";
import { useEffect, useId, useRef, useState } from "react";
import { Button } from "@/components/ui/button";

function SearchableAuthors({ authors }: { authors: TopReplyAuthor[] }) {
  const inputId = useId();
  const suggestionsId = useId();
  const [query, setQuery] = useState("");
  const [match, setMatch] = useState<TopReplyAuthor | null>(null);
  const [message, setMessage] = useState("");
  const [searchNumber, setSearchNumber] = useState(0);
  const row = useRef<HTMLTableRowElement>(null);
  const content = useRef<HTMLDivElement>(null);

  function findAuthor(value: string) {
    const handle = value.trim().replace(/^@/, "").toLowerCase();
    if (!handle) return undefined;
    const exact = authors.find((candidate) => candidate.handle?.toLowerCase() === handle);
    if (exact) return exact;
    const matches = authors.filter((candidate) => candidate.handle?.toLowerCase().includes(handle));
    return matches.length === 1 ? matches[0] : undefined;
  }

  useEffect(() => {
    row.current?.scrollIntoView({ block: "center", inline: "nearest" });
  }, [match, searchNumber]);

  return (
    <div ref={content} className="space-y-4">
      <form
        className="sticky top-0 z-30 space-y-2 bg-background pb-3"
        onSubmit={(event) => {
          event.preventDefault();
          const author = findAuthor(query);
          setMatch(author ?? null);
          const handle = query.trim().replace(/^@/, "").toLowerCase();
          const matches = handle
            ? authors.filter((candidate) => candidate.handle?.toLowerCase().includes(handle))
            : [];
          setMessage(author
            ? `Found ${author.handle ? `@${author.handle}` : "author"} in this list.`
            : matches.length > 1
              ? "Multiple matching handles; refine your search."
              : "No matching handle in this list.");
          setSearchNumber((value) => value + 1);
        }}
      >
        <label htmlFor={inputId} className="block text-sm font-medium">Find author by handle</label>
        <div className="flex gap-2">
          <input
            id={inputId}
            type="search"
            list={suggestionsId}
            autoComplete="off"
            value={query}
            onChange={(event) => {
              const value = event.target.value;
              setQuery(value);
              setMatch(findAuthor(value) ?? null);
              setMessage("");
              if (value.trim().length === 0) {
                content.current?.closest("[data-statistics-scroll]")?.scrollTo({ top: 0, behavior: "instant" });
              }
            }}
            placeholder="@handle.bsky.social"
            className="w-64 max-w-full min-w-0 rounded border border-foreground/50 px-3 py-1 text-sm focus-visible:border-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-foreground"
          />
          <datalist id={suggestionsId}>
            {authors.filter((author) => author.handle !== null).map((author) => (
              <option key={author.did} value={`${query.trim().startsWith("@") ? "@" : ""}${author.handle}`} />
            ))}
          </datalist>
          <Button type="submit" variant="outline" aria-label="Search">
            <span aria-hidden="true">⌕</span>
          </Button>
        </div>
        <p role="status" aria-live="polite" aria-atomic="true" className="text-sm text-muted-foreground">
          {message}
        </p>
      </form>
      <ReplyAuthorsTable
        authors={authors}
        label="All reply authors to the right Jerry"
        highlightedDid={match?.did}
        highlightedRowRef={row}
      />
    </div>
  );
}

export function ReplyAuthorsDialog({ authors }: { authors: TopReplyAuthor[] }) {
  return (
    <StatisticsDialog
      title={'All users telling Jerry "No"'}
      triggerLabel="Show all users telling the correct Jerry No"
      trigger={<ReplyAuthorsTable authors={authors} label="Top ten reply authors to the right Jerry" clickable />}
      overlayTrigger
      path="/api/statistics/authors"
      isValid={isReplyAuthorList}
    >
      {(allAuthors) => <SearchableAuthors authors={allAuthors} />}
    </StatisticsDialog>
  );
}
