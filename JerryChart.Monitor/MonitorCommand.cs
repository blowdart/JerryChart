// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace JerryChart.Monitor;

internal static class MonitorCommand
{
    internal static RootCommand Create(
        Func<CancellationToken, Task<int>> run,
        Func<CancellationToken, Task<int>>? backfillParentUris = null)
    {
        var root = new RootCommand("Monitor replies containing Jerry no (default: run).");
        var runCommand = new Command("run", "Replay all posts and monitor replies containing Jerry no.");

        root.SetAction((_, cancellationToken) => run(cancellationToken));
        runCommand.SetAction((_, cancellationToken) => run(cancellationToken));
        root.Subcommands.Add(runCommand);
        if (backfillParentUris is not null)
        {
            var backfillCommand = new Command("backfill-parent-uris",
                "Resolve parent post AT URIs for existing hits without starting Jetstream or the actor updater.");
            backfillCommand.SetAction((_, cancellationToken) => backfillParentUris(cancellationToken));
            root.Subcommands.Add(backfillCommand);
        }

        return root;
    }
}