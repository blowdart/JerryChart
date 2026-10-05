// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;

using JerryChart.Data;
using JerryChart.Monitor;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MySqlConnector;

using OpenTelemetry.Metrics;

RootCommand command = MonitorCommand.Create(ExecuteAsync, ExecuteParentUriBackfillAsync);
return await command.Parse(args).InvokeAsync();

static async Task<int> ExecuteAsync(CancellationToken cancellationToken)
{
    using var configuration = new MonitorWebHostBuilder();
    WebApplicationBuilder builder = configuration.Builder;
    builder.Services.AddOpenTelemetry()
        .WithMetrics(metrics => metrics
            .AddAtProtoJetstreamMetrics()
            .AddAtProtoHttpClientMetrics()
            .AddAtProtoDirectoryMetrics());
    builder.AddJerryChartDatabase();
    builder.Services.AddHostedService<ActorHandleUpdater>();
    builder.Services.AddParentUriBackfillScheduler();

    string? apiKey = builder.Configuration["Jetstream:ApiKey"] ?? Environment.GetEnvironmentVariable("_JetstreamApiKey");
    string? hostUri = builder.Configuration["Jetstream:Host"];
    if (string.IsNullOrWhiteSpace(apiKey) ||
        !Uri.TryCreate(hostUri, UriKind.Absolute, out Uri? configuredUri) || configuredUri.Scheme != "wss")
    {
        using ILoggerFactory logs = LoggerFactory.Create(logging => logging.AddConsole());
        MonitorLog.InvalidConfiguration(logs.CreateLogger("JerryChart.Monitor"));
        return 2;
    }

    builder.Services.AddSingleton(services => new MonitorSupervisor(
        token => JetstreamMonitor.RunAsync(services.GetRequiredService<MySqlDataSource>(), apiKey, configuredUri,
            services.GetRequiredService<ILoggerFactory>().CreateLogger("JerryChart.Monitor"), token),
        services.GetRequiredService<IHostApplicationLifetime>(), services.GetRequiredService<ILogger<MonitorSupervisor>>()));
    builder.Services.AddHostedService(services => services.GetRequiredService<MonitorSupervisor>());
    builder.Services.AddHealthChecks().AddCheck<MonitorSupervisor>("monitor-supervisor", tags: ["live"]);
    await using WebApplication host = builder.Build();
    host.MapDefaultEndpoints();
    ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JerryChart.Monitor");
    IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    using var cancellationRegistration = cancellationToken.Register(lifetime.StopApplication);
    using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
    CancellationToken stoppingToken = shutdown.Token;

    try
    {
        await using (var connection = await host.Services.GetRequiredService<MySqlDataSource>()
            .OpenConnectionAsync(stoppingToken))
        {
            await MonitorSchema.InitializeAsync(connection, stoppingToken);
        }

        host.Services.UseParentUriBackfillScheduler();
        await host.StartAsync(stoppingToken);
        return await host.Services.GetRequiredService<MonitorSupervisor>().Completion;
    }
    catch (Exception exception) when (exception is MySqlException or IOException or HttpRequestException
        or JsonException or ArgumentException or InvalidOperationException)
    {
        MonitorLog.MonitorFailed(logger, exception);
        return 1;
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
        MonitorLog.MonitorStopped(logger);
    }
    finally
    {
        await host.StopAsync(CancellationToken.None);
    }

    return 0;
}

static async Task<int> ExecuteParentUriBackfillAsync(CancellationToken cancellationToken)
{
    using var configuration = new MonitorHostBuilder();
    HostApplicationBuilder builder = configuration.Builder;
    using IHost host = builder.Build();
    ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JerryChart.Monitor");
    string? connectionString = builder.Configuration["ConnectionStrings:jerrychart"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        MonitorLog.MissingBackfillConnectionString(logger);
        return 2;
    }

    try
    {
        await using MySqlDataSource dataSource = new MySqlDataSourceBuilder(connectionString).Build();
        var invocation = new ParentUriBackfillInvocation(dataSource,
            host.Services.GetRequiredService<ILogger<ParentUriBackfillInvocation>>(), TimeProvider.System);
        await invocation.RunAsync(scheduled: false, cancellationToken);
        return 0;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        MonitorLog.ParentUriBackfillStopped(logger);
        return 0;
    }
    catch (Exception exception) when (exception is MySqlException or IOException or HttpRequestException
        or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
    {
        MonitorLog.ParentUriBackfillFailed(logger, exception);
        return 1;
    }
}