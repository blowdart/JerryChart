// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using JerryChart.AppHost;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<ParameterResource> mysqlPassword = builder.AddParameter("mysql-password", secret: true);
IResourceBuilder<MySqlDatabaseResource> database = builder.AddMySql("mysql", password: mysqlPassword)
    .WithImageTag("8.4")
    .WithArgs("--skip-log-bin")
    .WithDataVolume("jerrychart-mysql-data")
    .AddDatabase("jerrychart");

IResourceBuilder<ProjectResource> api = builder.AddProject<Projects.JerryChart_Api>("api", launchProfileName: "http")
    .WithHttpHealthCheck("/health")
    .WithReference(database)
    .WaitFor(database);

IResourceBuilder<ParameterResource> archiveKey = builder.AddParameter("jetstream-api-key", secret: true);
builder.AddProject<Projects.JerryChart_Monitor>("monitor")
    .WithHttpEndpoint(targetPort: 8081, name: "http")
    .WithEnvironment("ASPNETCORE_URLS", "http://localhost:8081")
    .WithHttpHealthCheck("/health")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("Jetstream__ApiKey", archiveKey)
    .WithEnvironment("Logging__LogLevel__JerryChart.Monitor", "Debug");

builder.AddJavaScriptApp("web", Path.Combine("..", "JerryChart.Web"))
    .WithRunScript("dev")
    .WithNpm(installCommand: "ci")
    .WithHttpEndpoint(env: "PORT")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("API_BASE_URL", api.GetEndpoint("http"))
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

await using DistributedApplication app = builder.Build();
AppHostLog.Started(app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("JerryChart.AppHost"), ThisAssembly.AssemblyInformationalVersion);
await app.RunAsync();