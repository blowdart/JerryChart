// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.Security;

namespace JerryChart.Monitor;

internal static class MonitorHttpClients
{
    internal static HttpClient CreateAppViewClient()
    {
        return new HttpClient(CreateHandler())
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
    }

    internal static SocketsHttpHandler CreateHandler()
    {
        return SsrfSocketsHttpHandlerFactory.Create(
            connectTimeout: TimeSpan.FromSeconds(10),
            allowAutoRedirect: false);
    }
}
