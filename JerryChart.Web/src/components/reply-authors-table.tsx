import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { TopReplyAuthor } from "@/lib/reply-authors";
import { ProfileHoverCard } from "@/components/profile-hover-card";

export function ReplyAuthorsTable({ authors, label, clickable = false, highlightedDid, highlightedRowRef }: {
  authors: TopReplyAuthor[];
  label: string;
  clickable?: boolean;
  highlightedDid?: string | null;
  highlightedRowRef?: React.Ref<HTMLTableRowElement>;
}) {
  if (authors.length === 0) {
    return <p className="py-6 text-center text-muted-foreground">No Jerry no replies to the right Jerry recorded yet.</p>;
  }

  return (
    <Table aria-label={label}>
      <TableHeader>
        <TableRow>
          <TableHead scope="col">Rank</TableHead>
          <TableHead scope="col">User</TableHead>
          <TableHead scope="col" className="text-right">Replies</TableHead>
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
            <TableHead scope="row" className="whitespace-normal break-words">
              <ProfileHoverCard author={author} clickable={clickable} />
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
