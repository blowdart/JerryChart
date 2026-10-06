// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using JerryChart.Api;
using JerryChart.Data;

using MySqlConnector;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddJerryChartDatabase();
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddApiMetrics());
builder.Services.AddHostedService<CheckpointFreshnessSampler>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));

WebApplication app = builder.Build();
app.UseExceptionHandler();
app.MapDefaultEndpoints();

await using (MySqlConnection connection = await app.Services.GetRequiredService<MySqlDataSource>()
    .OpenConnectionAsync(app.Lifetime.ApplicationStopping))
{
    await MonitorSchema.InitializeAsync(connection, app.Lifetime.ApplicationStopping);
}
ApiLog.DatabaseInitialized(app.Logger);

app.MapGet("/statistics/reply-summary", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await ApiMetrics.QueryAsync(StatisticsReport.Summary,
        () => store.GetReplySummaryAsync(cancellationToken), cancellationToken)));

app.MapGet("/statistics/last-updated", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await ApiMetrics.QueryAsync(StatisticsReport.LastUpdated,
        () => store.GetLastUpdatedAsync(cancellationToken), cancellationToken)));

app.MapGet("/statistics/processing-status", async (ProcessingStatusStore store, HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return TypedResults.Ok(await ApiMetrics.QueryAsync(StatisticsReport.ProcessingStatus,
        () => store.GetAsync(cancellationToken), cancellationToken));
});

app.MapGet("/statistics/right-jerry/top-authors", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await ApiMetrics.QueryAsync(StatisticsReport.TopAuthors,
        () => store.GetTopRightJerryAuthorsAsync(cancellationToken), cancellationToken, result => result.Count)));

app.MapGet("/statistics/right-jerry/top-posts", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await ApiMetrics.QueryAsync(StatisticsReport.TopPosts,
        () => store.GetTopRightJerryPostsAsync(cancellationToken), cancellationToken, result => result.Count)));

app.MapGet("/statistics/right-jerry/authors", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await ApiMetrics.QueryAsync(StatisticsReport.Authors,
        () => store.GetAllRightJerryAuthorsAsync(cancellationToken), cancellationToken, result => result.Count)));

app.MapGet("/statistics/right-jerry/monthly-replies", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await ApiMetrics.QueryAsync(StatisticsReport.MonthlyReplies,
        () => store.GetMonthlyRightJerryRepliesAsync(cancellationToken), cancellationToken, result => result.Count)));

app.MapGet("/statistics/right-jerry/all-time-monthly-replies", async (StatisticsStore store, CancellationToken cancellationToken) =>
    TypedResults.Ok(await ApiMetrics.QueryAsync(StatisticsReport.AllTimeMonthlyReplies,
        () => store.GetAllTimeRightJerryRepliesAsync(cancellationToken), cancellationToken, result => result.Count)));

await app.RunAsync();