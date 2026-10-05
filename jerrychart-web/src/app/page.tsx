import Image from "next/image";
import { RefreshStatistics } from "@/components/refresh-statistics";
import { ProcessingStatus } from "@/components/processing-status";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { ReplyHistoryDialog } from "@/components/reply-history-dialog";
import { ReplyAuthorsDialog } from "@/components/reply-authors-dialog";
import { StatisticsLastUpdated } from "@/components/statistics-last-updated";
import { TopReplyPosts } from "@/components/top-reply-posts";
import { getInitialProcessingStatus, getMonthlyRightJerryReplies, getReplySummary, getStatisticsLastUpdated, getTopRightJerryAuthors, getTopRightJerryPosts } from "@/lib/api";

export const dynamic = "force-dynamic";

export default async function Home() {
  const [summary, topAuthors, monthlyReplies, lastUpdated, topPosts, processingStatus] = await Promise.all([
    getReplySummary(),
    getTopRightJerryAuthors(),
    getMonthlyRightJerryReplies(),
    getStatisticsLastUpdated(),
    getTopRightJerryPosts(),
    getInitialProcessingStatus(),
  ]);
  const formatCount = (count: number) => count.toLocaleString("en-US");

  return (
    <main className="mx-auto w-full max-w-4xl space-y-8 px-6 py-12">
      <header className="flex items-center gap-4">
        <a href="https://bsky.app/profile/jcsalterego.bsky.social" className="shrink-0">
          <Image
            src="https://cdn.bsky.app/img/avatar/plain/did:plc:vc7f4oafdgxsihk4cry2xpze/bafkreigexuagm6poq5mbwq5oh3md2grrl4zk3mxqcbttfehveuatazsrgu"
            alt="Jerry Chen"
            width={80}
            height={80}
            unoptimized
            className="size-20 rounded-full border object-cover"
          />
        </a>
        <div className="min-w-0 space-y-2">
          <h1 className="text-4xl font-semibold tracking-tight">Jerry No</h1>
          <p className="text-muted-foreground">
            Statistics on Bluesky&apos;s collective failure to make Jerry Chen reconsider his choices
          </p>
        </div>
      </header>
      <Card>
        <CardContent>
          <Table aria-label="Jerry no reply summary">
            <TableHeader>
              <TableRow>
                <TableHead scope="col">Replies</TableHead>
                <TableHead scope="col" className="text-right">Count</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              <TableRow>
                <TableHead scope="row">Total Jerry no replies</TableHead>
                <TableCell className="text-right tabular-nums">{formatCount(summary.totalReplies)}</TableCell>
              </TableRow>
              <TableRow>
                <TableHead scope="row">⤷ the right Jerry</TableHead>
                <TableCell className="text-right tabular-nums">{formatCount(summary.rightJerryReplies)}</TableCell>
              </TableRow>
              <TableRow>
                <TableHead scope="row">⤷ the wrong Jerry</TableHead>
                <TableCell className="text-right tabular-nums">{formatCount(summary.wrongJerryReplies)}</TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </CardContent>
      </Card>
      <TopReplyPosts posts={topPosts} />
      <div className="grid items-start gap-6 md:grid-cols-2">
      <Card className="min-w-0">
        <CardHeader>
          <CardTitle>Correct &quot;Jerry No&quot; replies, last six months</CardTitle>
        </CardHeader>
        <CardContent>
          <ReplyHistoryDialog months={monthlyReplies} />
        </CardContent>
      </Card>
      <Card className="min-w-0">
        <CardHeader className="flex-row items-center justify-between">
          <div className="space-y-1">
            <CardTitle>Top 10 users telling Jerry &quot;No&quot;</CardTitle>
          </div>
        </CardHeader>
        <CardContent>
          <ReplyAuthorsDialog authors={topAuthors} />
        </CardContent>
      </Card>
      </div>
      <footer className="space-y-1 border-t pt-6 text-right text-sm text-muted-foreground">
        <RefreshStatistics />
        <p>
          (c) 2006{" "}
          <a href="https://bsky.app/profile/blowdart.me" className="underline underline-offset-4">
            Barry Dorrans
          </a>
        </p>
        <p>
          Made with{" "}
          <a href="https://github.com/blowdart/idunno.Bluesky" className="underline underline-offset-4">
            idunno.Bluesky
          </a>
          {" "}and{" "}
          <a href="https://aspire.dev/" className="underline underline-offset-4">
            Aspire
          </a>
        </p>
        <StatisticsLastUpdated updatedAt={lastUpdated} />
        <ProcessingStatus initial={processingStatus} />
      </footer>
    </main>
  );
}
