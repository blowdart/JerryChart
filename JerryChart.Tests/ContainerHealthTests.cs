// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net;

using JerryChart.Data;
using JerryChart.Monitor;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JerryChart.Tests;

/// <summary>Verifies production container probes and monitor supervision using isolated loopback HTTP hosts.</summary>
[TestClass]
public sealed class ContainerHealthTests
{
    /// <summary>Verifies shared production endpoints return minimal data and exclude dependency failures from liveness.</summary>
    /// <returns>A task representing loopback HTTP probe testing.</returns>
    [TestMethod]
    public async Task ProductionHealthEndpointsSeparateReadinessAndLiveness()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.AddServiceDefaults();
        var dependency = new ProbeDependency();
        builder.Services.AddSingleton(dependency);
        builder.Services.AddHealthChecks().AddCheck<ProbeDependency>("private-database");
        await using WebApplication app = builder.Build();
        app.MapDefaultEndpoints();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        await AssertProbeAsync(http, "/alive", HttpStatusCode.OK, "Healthy", timeout.Token);
        await AssertProbeAsync(http, "/health", HttpStatusCode.ServiceUnavailable, "Unhealthy", timeout.Token);
        dependency.Ready = true;
        await AssertProbeAsync(http, "/health", HttpStatusCode.OK, "Healthy", timeout.Token);
        await app.StopAsync(timeout.Token);
    }

    /// <summary>Verifies production MySQL readiness registration does not affect the process-only liveness endpoint.</summary>
    /// <returns>A task representing API-style probe testing without accessing a database.</returns>
    [TestMethod]
    public async Task ProductionApiMySqlReadinessDoesNotLeakDetailsOrFailLiveness()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        // A closed local port fails deterministically; no production database or public service is contacted.
        builder.Configuration["ConnectionStrings:jerrychart"] =
            "Server=127.0.0.1;Port=1;Database=unused;User ID=unused;Connection Timeout=1";
        builder.AddServiceDefaults();
        builder.AddJerryChartDatabase();
        await using WebApplication app = builder.Build();
        app.MapDefaultEndpoints();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        await AssertProbeAsync(http, "/alive", HttpStatusCode.OK, "Healthy", timeout.Token);
        await AssertProbeAsync(http, "/health", HttpStatusCode.ServiceUnavailable, "Unhealthy", timeout.Token);
        await app.StopAsync(timeout.Token);
    }

    /// <summary>Verifies monitor probes while its fake supervisor is waiting and graceful shutdown cancels and awaits it.</summary>
    /// <returns>A task representing monitor host lifetime testing.</returns>
    [TestMethod]
    public async Task MonitorHttpProbesTrackSupervisorWithoutDependingOnThroughput()
    {
        using var configuration = new MonitorWebHostBuilder(
            ["--environment=Production", "--urls=http://127.0.0.1:0"]);
        WebApplicationBuilder builder = configuration.Builder;
        builder.Logging.ClearProviders();
        var dependency = new ProbeDependency();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddSingleton(dependency);
        builder.Services.AddHealthChecks().AddCheck<ProbeDependency>("database-readiness");
        builder.Services.AddSingleton(services => new MonitorSupervisor(async token =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                exited.TrySetResult();
            }
        }, services.GetRequiredService<IHostApplicationLifetime>(), services.GetRequiredService<ILogger<MonitorSupervisor>>()));
        builder.Services.AddHostedService(services => services.GetRequiredService<MonitorSupervisor>());
        builder.Services.AddHealthChecks().AddCheck<MonitorSupervisor>("monitor-supervisor", tags: ["live"]);
        await using WebApplication app = builder.Build();
        app.MapDefaultEndpoints();
        var supervisor = app.Services.GetRequiredService<MonitorSupervisor>();
        Assert.AreEqual(HealthStatus.Unhealthy, (await supervisor.CheckHealthAsync(new HealthCheckContext())).Status);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        await AssertProbeAsync(http, "/alive", HttpStatusCode.OK, "Healthy", timeout.Token);
        await AssertProbeAsync(http, "/health", HttpStatusCode.ServiceUnavailable, "Unhealthy", timeout.Token);
        dependency.Ready = true;
        // Idle streams, archive quota waits and retry delays all keep the supervised task alive.
        await AssertProbeAsync(http, "/health", HttpStatusCode.OK, "Healthy", timeout.Token);
        await AssertProbeAsync(http, "/alive", HttpStatusCode.OK, "Healthy", timeout.Token);
        using (HttpResponseMessage unrelated = await http.GetAsync("/statistics/processing-status", timeout.Token))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, unrelated.StatusCode,
                "The monitor listener must not expose the API's statistics or a new administrative surface.");
        }
        app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        Assert.AreEqual(0, await supervisor.Completion.WaitAsync(timeout.Token));
        Assert.IsTrue(exited.Task.IsCompleted);
        Assert.AreEqual(HealthStatus.Unhealthy, (await supervisor.CheckHealthAsync(new HealthCheckContext())).Status);
        await app.StopAsync(timeout.Token);
    }

    /// <summary>Verifies an unexpected supervisor exit is not reported as healthy or a successful exit.</summary>
    /// <returns>A task representing supervisor failure testing.</returns>
    [TestMethod]
    public async Task SupervisorFailureStopsHostWithFailureExitCode()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        using IHost host = builder.Build();
        using var supervisor = new MonitorSupervisor(_ => throw new InvalidDataException("Fixture failure."),
            host.Services.GetRequiredService<IHostApplicationLifetime>(),
            host.Services.GetRequiredService<ILogger<MonitorSupervisor>>());
        await supervisor.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, await supervisor.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(HealthStatus.Unhealthy, (await supervisor.CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.IsTrue(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await supervisor.StopAsync(CancellationToken.None);
    }

    /// <summary>Verifies a normal-returning worker without shutdown is still an unexpected supervisor failure.</summary>
    /// <returns>A task representing unexpectedly terminated worker testing.</returns>
    [TestMethod]
    public async Task SupervisorUnexpectedReturnIsNotHealthySuccess()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        using IHost host = builder.Build();
        using var supervisor = new MonitorSupervisor(_ => Task.CompletedTask,
            host.Services.GetRequiredService<IHostApplicationLifetime>(),
            host.Services.GetRequiredService<ILogger<MonitorSupervisor>>());
        await supervisor.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, await supervisor.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(HealthStatus.Unhealthy, (await supervisor.CheckHealthAsync(new HealthCheckContext())).Status);
        await supervisor.StopAsync(CancellationToken.None);
    }

    private static async Task AssertProbeAsync(HttpClient http, string path, HttpStatusCode expected,
        string text, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(path, cancellationToken);
        Assert.AreEqual(expected, response.StatusCode);
        Assert.AreEqual(text, await response.Content.ReadAsStringAsync(cancellationToken),
            "Container health responses must not expose check names, exceptions, connection strings or credentials.");
    }

    private sealed class ProbeDependency : IHealthCheck
    {
        internal bool Ready { get; set; }
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(Ready ? HealthCheckResult.Healthy() :
                HealthCheckResult.Unhealthy("private connection-string fixture", new IOException("secret fixture"),
                    new Dictionary<string, object> { ["private-data"] = "secret fixture" }));
    }
}