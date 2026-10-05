// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using JerryChart.Api;
using JerryChart.Data;

using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddJerryChartDatabase();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));

var app = builder.Build();
app.UseExceptionHandler();
app.MapDefaultEndpoints();

await using (var connection = await app.Services.GetRequiredService<MySqlDataSource>()
    .OpenConnectionAsync(app.Lifetime.ApplicationStopping))
{
    await MonitorSchema.InitializeAsync(connection, app.Lifetime.ApplicationStopping);
}
ApiLog.DatabaseInitialized(app.Logger);

app.MapGet("/statistics/reply-summary", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await store.GetReplySummaryAsync(cancellationToken)));

app.MapGet("/statistics/last-updated", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await store.GetLastUpdatedAsync(cancellationToken)));

app.MapGet("/statistics/processing-status", async (ProcessingStatusStore store, HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return TypedResults.Ok(await store.GetAsync(cancellationToken));
});

app.MapGet("/statistics/right-jerry/top-authors", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await store.GetTopRightJerryAuthorsAsync(cancellationToken)));

app.MapGet("/statistics/right-jerry/top-posts", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await store.GetTopRightJerryPostsAsync(cancellationToken)));

app.MapGet("/statistics/right-jerry/authors", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await store.GetAllRightJerryAuthorsAsync(cancellationToken)));

app.MapGet("/statistics/right-jerry/monthly-replies", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await store.GetMonthlyRightJerryRepliesAsync(cancellationToken)));

app.MapGet("/statistics/right-jerry/all-time-monthly-replies", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await store.GetAllTimeRightJerryRepliesAsync(cancellationToken)));

await app.RunAsync();