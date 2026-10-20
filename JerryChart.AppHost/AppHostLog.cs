// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace JerryChart.AppHost;

internal static partial class AppHostLog
{
    [LoggerMessage(1, LogLevel.Information, "JerryChart AppHost version {Version}.")]
    internal static partial void Started(ILogger logger, string version);
}
