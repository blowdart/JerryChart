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
        Func<Did, CancellationToken, Task<int>>? excludeDid = null,
        Func<string, CancellationToken, Task<int>>? auditThread = null,
        Func<Did, CancellationToken, Task<int>>? refreshActor = null,
        Func<CancellationToken, Task<int>>? checkConsistency = null,
        Func<CancellationToken, Task<int>>? replayStatus = null)
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

        if (auditThread is not null)
        {
            var post = new Argument<string>("post") { Description = "A Bluesky post URL or post AT URI." };
            post.Validators.Add(result =>
            {
                if (!ThreadAudit.TryParsePost(result.GetValueOrDefault<string>(), out _))
                {
                    result.AddError("A Bluesky post link or at://authority/app.bsky.feed.post/key URI is required.");
                }
            });
            var audit = new Command("audit-thread", "Compare visible qualifying direct replies with stored hits (read-only).");
            audit.Arguments.Add(post);
            audit.SetAction((result, token) => auditThread(result.GetValue(post)!, token));
            root.Subcommands.Add(audit);
        }

        if (refreshActor is not null)
        {
            var did = new Argument<string>("did") { Description = "The tracked actor DID to refresh." };
            did.Validators.Add(result =>
            {
                if (!Did.TryParse(result.GetValueOrDefault<string>(), out _))
                {
                    result.AddError("A valid AT Protocol DID is required.");
                }
            });
            var refresh = new Command("refresh-actor", "Immediately refresh one tracked actor's handle and account status.");
            refresh.Arguments.Add(did);
            refresh.SetAction((result, token) => refreshActor(new Did(result.GetValue(did)!), token));
            root.Subcommands.Add(refresh);
        }

        AddJob("check-consistency", "Report database consistency problems without repairing them.", checkConsistency);
        AddJob("replay-status", "Report durable replay position, progress and retry state (read-only).", replayStatus);

        return root;

        void AddJob(string name, string description, Func<CancellationToken, Task<int>>? action)
        {
            if (action is null)
            {
                return;
            }

            var job = new Command(name, description);
            job.SetAction((_, token) => action(token));
            root.Subcommands.Add(job);
        }
    }
}