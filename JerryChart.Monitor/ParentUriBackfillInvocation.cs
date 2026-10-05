// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

using MySqlConnector;

namespace JerryChart.Monitor;

internal sealed class ParentUriBackfillInvocation(
    MySqlDataSource dataSource,
    ILogger<ParentUriBackfillInvocation> logger,
    TimeProvider clock,
    Func<HttpClient>? createClient = null)
{
    internal async Task RunAsync(bool scheduled, CancellationToken cancellationToken)
    {
        using HttpClient httpClient = createClient?.Invoke() ?? new HttpClient(
            new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
        var client = new ParentPostClient(httpClient);
        await new ParentUriBackfiller(dataSource, client, logger, clock, skipIfLocked: scheduled)
            .RunAsync(cancellationToken);
    }
}