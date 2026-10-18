// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.AtProto;

using JerryChart.Monitor;

using Microsoft.Extensions.Logging;

namespace JerryChart.Tests;

/// <summary>Verifies source-generated monitor logging preserves structured diagnostics.</summary>
[TestClass]
public sealed class MonitorLogTests
{
    /// <summary>Verifies event metadata, structured fields, and exception forwarding.</summary>
    [TestMethod]
    public void GeneratedLogsPreserveStructuredFieldsAndExceptions()
    {
        var logger = new RecordingLogger();
        var exception = new IOException("Replay failed.");

        MonitorLog.RetryingMonitor(logger, exception, 15, 3);

        Assert.AreEqual(LogLevel.Error, logger.Level);
        Assert.AreEqual(14, logger.EventId.Id);
        Assert.AreSame(exception, logger.Exception);
        Assert.AreEqual(15d, logger.Fields["DelaySeconds"]);
        Assert.AreEqual(3, logger.Fields["ConsecutiveFailures"]);
        Assert.AreEqual(
            "Monitor attempt failed. JerryChart will retry from durable progress in {DelaySeconds} seconds. Consecutive monitor failures: {ConsecutiveFailures}. SDK-managed live reconnects are disabled; JerryChart owns recovery.",
            logger.Fields["{OriginalFormat}"]);
        Assert.Contains("JerryChart will retry from durable progress in 15 seconds.", logger.Message);

        var atUri = new AtUri("at://did:plc:author/app.bsky.feed.post/post1");
        var authorDid = new Did("did:plc:author");
        var parentDid = new Did("did:plc:parent");
        MonitorLog.MatchedReply(logger, atUri, authorDid, parentDid);
        Assert.AreEqual(atUri, logger.Fields["AtUri"]);
        Assert.AreEqual(authorDid, logger.Fields["AuthorDid"]);
        Assert.AreEqual(parentDid, logger.Fields["ParentAuthorDid"]);

        MonitorLog.MissingHandle(logger, parentDid);
        Assert.AreEqual(parentDid, logger.Fields["Did"]);

        MonitorLog.RetryingBackfillDatabase(logger, exception, 30);
        Assert.AreEqual(25, logger.EventId.Id);
        Assert.AreSame(exception, logger.Exception);
        Assert.AreEqual(30d, logger.Fields["DelaySeconds"]);

        MonitorLog.LiveCursorTooOld(logger, 456);
        Assert.AreEqual(LogLevel.Warning, logger.Level);
        Assert.AreEqual(45, logger.EventId.Id);
        Assert.AreEqual(456L, logger.Fields["Sequence"]);
        Assert.AreEqual(
            "Live Jetstream rejected cursor {Sequence} as too old. Returning to archive from the last durably processed sequence.",
            logger.Fields["{OriginalFormat}"]);
    }

    /// <summary>Verifies transport retry warnings omit exceptions while debug logs retain diagnostics.</summary>
    [TestMethod]
    public void LiveRecoveryLogsDistinguishRetryOwnershipAndDiagnostics()
    {
        var logger = new RecordingLogger();
        var exception = new LiveStreamInterruptedException(new IOException("SDK diagnostic"));
        MonitorLog.RetryingLiveStream(logger, 1.1, 8);
        Assert.AreEqual(LogLevel.Warning, logger.Level);
        Assert.AreEqual(46, logger.EventId.Id);
        Assert.IsNull(logger.Exception);
        Assert.AreEqual(8, logger.Fields["ConsecutiveFailures"]);
        Assert.AreEqual(1.1, logger.Fields["DelaySeconds"]);
        Assert.Contains("SDK-managed reconnects are disabled; JerryChart owns recovery.", logger.Message);

        MonitorLog.LiveStreamInterruptionDetails(logger, exception);
        Assert.AreEqual(LogLevel.Debug, logger.Level);
        Assert.AreSame(exception, logger.Exception);
        Assert.AreEqual(47, logger.EventId.Id);

        MonitorLog.LiveEventsResumed(logger);
        Assert.AreEqual(LogLevel.Information, logger.Level);
        Assert.AreEqual(48, logger.EventId.Id);
        Assert.AreEqual("Live Jetstream resumed; receiving events.", logger.Message);
    }

    /// <summary>Verifies disabled log levels do not invoke the logger.</summary>
    [TestMethod]
    public void DisabledLoggingDoesNotWriteLog()
    {
        var logger = new RecordingLogger { Enabled = false };

        MonitorLog.ConnectingToLive(logger, 456);

        Assert.AreEqual(0, logger.Writes);
    }

    private sealed class RecordingLogger : ILogger
    {
        internal bool Enabled { get; init; } = true;
        internal int Writes { get; private set; }
        internal LogLevel Level { get; private set; }
        internal EventId EventId { get; private set; }
        internal Exception? Exception { get; private set; }
        internal string Message { get; private set; } = "";
        internal Dictionary<string, object?> Fields { get; private set; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => Enabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Writes++;
            Level = logLevel;
            EventId = eventId;
            Exception = exception;
            Message = formatter(state, exception);
            Fields = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary();
        }
    }
}