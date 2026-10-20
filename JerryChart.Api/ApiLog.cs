// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace JerryChart.Api;

internal static partial class ApiLog
{
    [LoggerMessage(3, LogLevel.Information, "JerryChart API version {Version}.")]
    internal static partial void Started(ILogger logger, string version);

    [LoggerMessage(1, LogLevel.Information, "Statistics database schema initialized.")]
    internal static partial void DatabaseInitialized(ILogger logger);

    [LoggerMessage(2, LogLevel.Warning, "Checkpoint freshness sampling failed; retrying in 30 seconds.")]
    internal static partial void CheckpointSamplingFailed(ILogger logger, Exception exception);
}
