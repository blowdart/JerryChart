// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

using idunno.AtProto;

namespace JerryChart.Monitor;

internal static class MonitorCommand
{
    internal static RootCommand Create(
        Func<CancellationToken, Task<int>> run,
        Func<CancellationToken, Task<int>>? backfillParentUris = null,
        Func<Did, CancellationToken, Task<int>>? excludeDid = null)
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

        if (excludeDid is not null)
        {
            var did = new Argument<string>("did") { Description = "The DID to permanently exclude." };
            did.Validators.Add(result =>
            {
                if (!Did.TryParse(result.GetValueOrDefault<string>(), out _))
                {
                    result.AddError("A valid AT Protocol DID is required.");
                }
            });
            var exclude = new Command("exclude-did",
                "Delete existing replies authored by or addressed to a DID, and block future ingestion. This is permanent.");
            exclude.Arguments.Add(did);
            exclude.SetAction((result, token) => excludeDid(new Did(result.GetValue(did)!), token));
            root.Subcommands.Add(exclude);
        }

        return root;
    }
}