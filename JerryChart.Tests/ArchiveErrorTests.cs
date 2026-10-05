// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.AtProto.Jetstream.Archive;

using JerryChart.Monitor;

using Microsoft.Extensions.Logging;

namespace JerryChart.Tests;

/// <summary>Verifies explicit archive skip decisions and observable diagnostics.</summary>
[TestClass]
public sealed class ArchiveErrorTests
{
    /// <summary>Verifies an invalid row is skipped with its sequence and exception logged.</summary>
    [TestMethod]
    public void RecordFailureSkipsOnlyRecordAndLogsSequence()
    {
        var logger = new RecordingLogger();
        var exception = new InvalidDataException("The value is nested more than 128 levels deep.");

        var action = JetstreamMonitor.HandleArchiveError(123, exception, logger);

        Assert.AreEqual(JetstreamArchiveErrorAction.SkipRecord, action);
        Assert.AreEqual(LogLevel.Warning, logger.Level);
        Assert.AreSame(exception, logger.Exception);
        Assert.Contains("record 123", logger.Message);
        Assert.Contains("omitted", logger.Message);
    }

    /// <summary>Verifies a failure without a sequence skips the block and logs the data omission.</summary>
    [TestMethod]
    public void BlockFailureSkipsBlockAndLogsException()
    {
        var logger = new RecordingLogger();
        var exception = new InvalidDataException("Invalid archive block.");

        var action = JetstreamMonitor.HandleArchiveError(null, exception, logger);

        Assert.AreEqual(JetstreamArchiveErrorAction.SkipBlock, action);
        Assert.AreEqual(LogLevel.Warning, logger.Level);
        Assert.AreSame(exception, logger.Exception);
        Assert.Contains("archive block", logger.Message);
        Assert.Contains("omitted", logger.Message);
    }

    private sealed class RecordingLogger : ILogger
    {
        internal LogLevel Level { get; private set; }
        internal Exception? Exception { get; private set; }
        internal string Message { get; private set; } = "";

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            Exception = exception;
            Message = formatter(state, exception);
        }
    }
}