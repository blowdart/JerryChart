// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;

using idunno.AtProto;
using idunno.AtProto.Jetstream;

using JerryChart.Monitor;

using Microsoft.Extensions.Logging.Abstractions;

namespace JerryChart.Tests;

/// <summary>Verifies matching against untrusted Bluesky records.</summary>
[TestClass]
public sealed class ReplyMatcherTests
{
    /// <summary>Verifies phrase boundaries, case folding, and punctuation separators.</summary>
    /// <param name="text">The post text.</param>
    /// <param name="expected">Whether it contains the requested phrase.</param>
    [TestMethod]
    [DataRow("Jerry no", true)]
    [DataRow("Oh JERRY, NO!", true)]
    [DataRow("jerry...no", true)]
    [DataRow("Jerry?! \r\n NO", true)]
    [DataRow("Jerry\u2014no", true)]
    [DataRow("Jerry \u201cno\u201d", true)]
    [DataRow("Jerryno", false)]
    [DataRow("Jerry nobody", false)]
    [DataRow("NotJerry no", false)]
    [DataRow("Jerry says no", false)]
    [DataRow("Jerry + no", false)]
    public void PhraseMatchesOnlyRequestedWords(string text, bool expected)
    {
        Assert.AreEqual(expected, ReplyMatcher.ContainsPhrase(text));
    }

    /// <summary>Verifies the immediate parent's DID is used instead of the thread root.</summary>
    [TestMethod]
    public void ReplyExtractsAuthorParentAndCreationTime()
    {
        var hit = ReplyMatcher.Match(CreateEvent(), NullLogger.Instance);
        Assert.IsNotNull(hit);
        Assert.AreEqual(new AtUri("at://did:plc:author/app.bsky.feed.post/post1"), hit.AtUri);
        Assert.AreEqual(new Did("did:plc:author"), hit.AuthorDid);
        Assert.AreEqual(new Did("did:plc:parent"), hit.ParentAuthorDid);
        Assert.AreEqual(new AtUri("at://did:plc:parent/app.bsky.feed.post/parent1"), hit.ParentAtUri);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-04T17:00:00Z", CultureInfo.InvariantCulture), hit.CreatedAt);
    }

    /// <summary>Verifies standalone posts, deleted records, and unrelated collections are ignored.</summary>
    [TestMethod]
    public void NonRepliesAndDeletesAreIgnored()
    {
        Assert.IsNull(ReplyMatcher.Match(CreateEvent(reply: false), NullLogger.Instance));
        Assert.IsNull(ReplyMatcher.Match(CreateEvent(operation: JetstreamCommitOperation.Delete), NullLogger.Instance));
        Assert.IsNull(ReplyMatcher.Match(CreateEvent(operation: JetstreamCommitOperation.Unknown), NullLogger.Instance));
        var otherCollection = CreateEvent();
        Assert.IsNull(ReplyMatcher.Match(otherCollection with
        {
            Commit = otherCollection.Commit with { Collection = "app.bsky.feed.like" }
        }, NullLogger.Instance));
    }

    /// <summary>Verifies updates and backfilled assertions can produce historical hits.</summary>
    [TestMethod]
    public void UpdatesAndSyncBackfillsAreMatched()
    {
        Assert.IsNotNull(ReplyMatcher.Match(CreateEvent(operation: JetstreamCommitOperation.Update), NullLogger.Instance));
        Assert.IsNotNull(ReplyMatcher.Match(CreateEvent() with { IsSyncBackfill = true }, NullLogger.Instance));
    }

    /// <summary>Verifies malformed matching replies are logged rather than assigned invented dates or authors.</summary>
    /// <param name="createdAt">An invalid creation timestamp.</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow("not-a-date")]
    [DataRow("0001-01-01T00:00:00Z")]
    public void InvalidTimestampCannotProduceHit(string? createdAt)
    {
        var logger = new WarningLogger();
        Assert.IsNull(ReplyMatcher.Match(CreateEvent(createdAt: createdAt), logger));
        Assert.AreEqual(1, logger.Warnings);
    }

    internal static JetstreamCommitEvent CreateEvent(bool reply = true,
        JetstreamCommitOperation operation = JetstreamCommitOperation.Create,
        string? createdAt = "2026-10-04T19:00:00+02:00")
    {
        const string cid = "bafyreihdwdcefgh4dqkjv67uzcmw7ojee6xedzdetojuzjevtenxquvyku";
        var record = JsonSerializer.SerializeToElement(new
        {
            text = "Jerry, no!",
            createdAt,
            reply = reply ? new
            {
                root = new { uri = "at://did:plc:root/app.bsky.feed.post/root1", cid },
                parent = new { uri = "at://did:plc:parent/app.bsky.feed.post/parent1", cid }
            } : null
        });

        return new JetstreamCommitEvent
        {
            Did = "did:plc:author",
            Kind = JetStreamEventKind.Commit,
            TimeStamp = 0,
            Sequence = 100,
            Commit = new JetstreamCommit
            {
                Collection = "app.bsky.feed.post",
                Operation = operation,
                RKey = "post1",
                Rev = "revision1",
                Record = record
            }
        };
    }

    private sealed class WarningLogger : Microsoft.Extensions.Logging.ILogger
    {
        internal int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}