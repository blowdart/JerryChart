// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net.WebSockets;

using idunno.AtProto.Jetstream;
using idunno.AtProto.Jetstream.Archive;
using idunno.Security;

using JerryChart.Monitor;

namespace JerryChart.Tests;

/// <summary>Verifies monitor HTTP transports retain SSRF protection.</summary>
[TestClass]
public sealed class MonitorHttpClientsTests
{
    /// <summary>Gets or sets the context for the current test.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Verifies the handler keeps redirects off and a bounded connect timeout.</summary>
    [TestMethod]
    public void HandlerAndAppViewSettingsPreserveSafetyAndLimits()
    {
        using SocketsHttpHandler handler = MonitorHttpClients.CreateHandler();
        Assert.IsFalse(handler.AllowAutoRedirect);
        Assert.AreEqual(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
        Assert.IsNotNull(handler.ConnectCallback);
        Assert.IsFalse(handler.UseProxy);
        using HttpClient client = MonitorHttpClients.CreateAppViewClient();
        Assert.AreEqual(TimeSpan.FromSeconds(30), client.Timeout);
        Assert.AreEqual(4 * 1024 * 1024, client.MaxResponseContentBufferSize);
    }

    /// <summary>Verifies AppView and SDK-owned clients block unsafe IP literals before connecting.</summary>
    /// <returns>A task representing blocked connection attempts.</returns>
    [TestMethod]
    public async Task BothClientPathsRejectUnsafeDestinations()
    {
        await using var jetstream = new InspectableJetstream();
        using HttpClient sdkClient = jetstream.CreateClient();
        using HttpClient appViewClient = MonitorHttpClients.CreateAppViewClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (HttpClient client in new[] { sdkClient, appViewClient })
        {
            foreach (string target in new[] { "http://127.0.0.1/", "http://10.0.0.1/", "http://169.254.169.254/", "http://[::1]/" })
            {
                Exception? failure = null;
                try
                {
                    using HttpResponseMessage response = await client.GetAsync(target, timeout.Token);
                }
                catch (Exception exception) when (exception is HttpRequestException or SsrfException)
                {
                    failure = exception;
                }
                Assert.IsNotNull(failure, $"Unsafe destination {target} must be blocked.");
                Assert.IsTrue(IsSsrfRejection(failure), $"Expected an SSRF rejection, not a failed network connection: {failure}");
            }
        }
        Assert.IsNotEmpty(sdkClient.DefaultRequestHeaders.UserAgent,
            "The SDK-owned client must preserve the SDK user agent.");
    }

    /// <summary>Verifies WebSocket handshakes use the protected HTTP invoker.</summary>
    /// <returns>A task representing a blocked WebSocket handshake.</returns>
    [TestMethod]
    public async Task WebSocketInvokerRejectsLoopback()
    {
        await using var jetstream = new InspectableJetstream();
        using HttpClient client = jetstream.CreateClient();
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        WebSocketException failure = await Assert.ThrowsAsync<WebSocketException>(() =>
            socket.ConnectAsync(new Uri("ws://127.0.0.1/"), client, timeout.Token));
        Assert.IsTrue(IsSsrfRejection(failure), failure.ToString());
    }

    /// <summary>Verifies the SDK-owned archive transport rejects unsafe service addresses.</summary>
    /// <param name="service">The unsafe archive origin.</param>
    /// <returns>A task representing a blocked snapshot request.</returns>
    [TestMethod]
    [DataRow("https://127.0.0.1/")]
    [DataRow("https://10.0.0.1/")]
    [DataRow("https://169.254.169.254/")]
    [DataRow("https://[::1]/")]
    public async Task SdkOwnedArchiveRejectsUnsafeDestinations(string service)
    {
        await using var jetstream = new AtProtoJetstream(uri: new Uri(service),
            options: new JetstreamOptions { ApiKey = "test-api-key" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        Exception failure = await Assert.ThrowsAsync<Exception>(async () =>
        {
            await foreach (JetstreamEvent item in jetstream.SnapshotAsync(new SnapshotRequest(),
                cancellationToken: timeout.Token))
            {
                Assert.Fail($"An unsafe archive must not return event {item.Sequence}.");
            }
        });
        Assert.IsTrue(IsSsrfRejection(failure), $"Expected an SSRF rejection, not another failure: {failure}");
    }

    private static bool IsSsrfRejection(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SsrfException)
            {
                return true;
            }
        }

        return false;
    }

    // Inspect the supported SDK-owned factory without substituting a factory, which disables CDN redirects.
    private sealed class InspectableJetstream : AtProtoJetstream
    {
        internal HttpClient CreateClient() => HttpClientFactory.CreateClient("idunno.atproto");
    }
}
