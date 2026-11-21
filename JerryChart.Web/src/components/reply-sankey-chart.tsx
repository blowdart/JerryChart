"use client";

import { ResponsiveContainer, Sankey, Tooltip, type SankeyNodeProps } from "recharts";
import { buildReplyFlowData, type ReplyFlow, type ReplyFlowNode } from "@/lib/reply-flow";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";

const colors: Record<ReplyFlowNode["stage"], string> = {
  all: "var(--chart-2)",
  right: "var(--chart-3)",
  wrong: "var(--chart-2)",
  year: "var(--chart-4)",
  month: "var(--chart-5)",
};

export function ReplySankeyChart({ flow }: { flow: ReplyFlow }) {
  const data = buildReplyFlowData(flow);
  const formatCount = (count: number) => count.toLocaleString("en-US");
  const height = Math.max(440, flow.months.length * 44 + 80);

  function renderNode({ x, y, width, height, index }: SankeyNodeProps) {
    const node = data.nodes[index];
    return (
      <g>
        <rect x={x} y={y} width={width} height={height} fill={colors[node.stage]}
          stroke="var(--foreground)" strokeWidth={1.5} />
        <text x={x + width + 8} y={y + height / 2 - 3} fill="var(--foreground)"
          fontSize={12} stroke="var(--background)" strokeWidth={4} paintOrder="stroke">
          {node.name}
          <tspan x={x + width + 8} dy={16}>{formatCount(node.count)}</tspan>
        </text>
      </g>
    );
  }

  return (
    <figure className="space-y-4">
      <figcaption className="text-sm text-muted-foreground">
        All replies flow to The Right Jerry and The Wrong Jerry. The Right Jerry flows to years,
        then each year to its months. Dates use UTC; widths represent reply counts.
      </figcaption>
      {flow.summary.totalReplies === 0 ? (
        <p>No matching replies recorded yet.</p>
      ) : (
        <div role="region" aria-label="All-time reply Sankey diagram. Scroll horizontally to see all four stages."
          tabIndex={0} className="overflow-x-auto rounded focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-foreground">
          <div className="min-w-[900px]" style={{ height }}>
            <ResponsiveContainer width="100%" height="100%">
              <Sankey data={data} align="left" verticalAlign="top" sort={false} iterations={0}
                nodeWidth={14} nodePadding={32} node={renderNode}
                link={{ stroke: "var(--chart-2)", strokeOpacity: 1 }}
                margin={{ top: 24, bottom: 24, left: 16, right: 180 }}
                accessibilityLayer
                title="All-time Jerry no reply flows"
                desc="All replies split into right and wrong Jerry. Right-Jerry replies split by UTC year and then month. Exact counts are available in the table below.">
                <Tooltip
                  formatter={(value) => [typeof value === "number" ? formatCount(value) : String(value), "replies"]}
                  contentStyle={{ background: "var(--background)", borderColor: "var(--border)", color: "var(--foreground)" }}
                  isAnimationActive={false}
                />
              </Sankey>
            </ResponsiveContainer>
          </div>
        </div>
      )}
      {flow.summary.totalReplies > 0 && flow.summary.rightJerryReplies === 0 && (
        <p>No matching replies to The Right Jerry recorded yet.</p>
      )}
      <details>
        <summary className="cursor-pointer rounded text-sm underline underline-offset-4 focus-visible:outline-2 focus-visible:outline-offset-2">
          View exact reply counts as a table
        </summary>
        <Table className="mt-3">
          <caption className="sr-only">All-time reply flow counts, with right-Jerry years and months in UTC.</caption>
          <TableHeader>
            <TableRow>
              <TableHead scope="col">Flow stage</TableHead>
              <TableHead scope="col" className="text-right">Replies</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {[
              { name: "All replies", count: flow.summary.totalReplies, stage: "all" },
              { name: "The Right Jerry", count: flow.summary.rightJerryReplies, stage: "right" },
              { name: "The Wrong Jerry", count: flow.summary.wrongJerryReplies, stage: "wrong" },
              ...data.nodes.filter((node) => node.stage === "year" || node.stage === "month"),
            ].map((node) => (
              <TableRow key={node.name}>
                <TableHead scope="row" className={node.stage === "month" ? "pl-6 font-normal" : undefined}>
                  {node.name}
                </TableHead>
                <TableCell className="text-right tabular-nums">{formatCount(node.count)}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </details>
    </figure>
  );
}
