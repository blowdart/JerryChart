// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<ParameterResource> mysqlPassword = builder.AddParameter("mysql-password", secret: true);
IResourceBuilder<MySqlDatabaseResource> database = builder.AddMySql("mysql", password: mysqlPassword)
    .WithImageTag("8.4")
    .WithDataVolume()
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

builder.AddJavaScriptApp("web", Path.Combine("..", "jerrychart-web"))
    .WithRunScript("dev")
    .WithNpm(installCommand: "ci")
    .WithHttpEndpoint(env: "PORT")
    .WithEnvironment("API_BASE_URL", api.GetEndpoint("http"))
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

await builder.Build().RunAsync();