import { Fragment } from "react";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { authorLabel, type TopReplyAuthor } from "@/lib/reply-authors";
import { ProfileHoverCard } from "@/components/profile-hover-card";

function breakableHandle(author: TopReplyAuthor) {
  return authorLabel(author).split(".").map((part, index) => (
    <Fragment key={index}>{index > 0 && <>.<wbr /></>}{part}</Fragment>
  ));
}

export function ReplyAuthorsTable({ authors, label, clickable = false, highlightedDid, highlightedRowRef, scrollable = false, scrollRef }: {
  authors: TopReplyAuthor[];
  label: string;
  clickable?: boolean;
  highlightedDid?: string | null;
  highlightedRowRef?: React.Ref<HTMLTableRowElement>;
  scrollable?: boolean;
  scrollRef?: React.Ref<HTMLDivElement>;
}) {
  if (authors.length === 0) {
    return <p className="py-6 text-center text-muted-foreground">No Jerry no replies to the right Jerry recorded yet.</p>;
  }

  return (
    <Table aria-label={label} className={scrollable ? undefined : "table-fixed"} containerProps={{
      ref: scrollRef,
      className: `${scrollable ? "min-h-0 flex-1 overflow-auto " : ""}focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-foreground`,
      tabIndex: 0,
      role: "region",
      "aria-label": label,
    }}>
      <TableHeader>
        <TableRow>
          <TableHead scope="col" className={scrollable ? "sticky top-0 z-10 bg-background" : "w-12"}>Rank</TableHead>
          <TableHead scope="col" className={scrollable ? "sticky top-0 z-10 bg-background" : undefined}>User</TableHead>
          <TableHead scope="col" className={`text-right${scrollable ? " sticky top-0 z-10 bg-background" : " w-20"}`}>Replies</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {authors.map((author, index) => (
          <TableRow
            key={author.did}
            ref={author.did === highlightedDid ? highlightedRowRef : undefined}
            aria-current={author.did === highlightedDid ? "true" : undefined}
            className={author.did === highlightedDid ? "bg-accent outline-2 -outline-offset-2 outline-primary" : undefined}
          >
            <TableCell className="tabular-nums">{index + 1}</TableCell>
            <TableHead scope="row" className={scrollable ? "whitespace-nowrap" : "whitespace-normal [overflow-wrap:anywhere]"}>
              <ProfileHoverCard author={author} clickable={clickable}>
                {scrollable || author.handle === null ? undefined : breakableHandle(author)}
              </ProfileHoverCard>
              {author.handle === null && (
                <span className="mt-1 block text-xs font-normal text-muted-foreground">{author.did}</span>
              )}
            </TableHead>
            <TableCell className="text-right tabular-nums">{author.replyCount.toLocaleString("en-US")}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
