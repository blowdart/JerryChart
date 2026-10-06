// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.Diagnostics.Metrics;

using MySqlConnector;

using OpenTelemetry.Metrics;

using TagName = Microsoft.Extensions.Diagnostics.Metrics.TagNameAttribute;

namespace JerryChart.Api;

internal enum StatisticsReport
{
    Summary,
    LastUpdated,
    ProcessingStatus,
    TopAuthors,
    TopPosts,
    Authors,
    MonthlyReplies,
    AllTimeMonthlyReplies,
    CheckpointFreshness
}

internal enum QueryError
{
    Database,
    Timeout,
    Cancellation,
    Other
}

internal struct ReportTags
{
    /// <summary>Gets or sets the bounded report name.</summary>
    [TagName("report")]
    public StatisticsReport Report { get; set; }
}

internal struct QueryErrorTags
{
    /// <summary>Gets or sets the bounded report name.</summary>
    [TagName("report")]
    public StatisticsReport Report { get; set; }
    /// <summary>Gets or sets the bounded error category.</summary>
    [TagName("error.type")]
    public QueryError Error { get; set; }
}

internal static partial class ApiMetricInstruments
{
    /// <summary>Creates the database report duration histogram.</summary>
    /// <param name="meter">The application meter.</param>
    /// <returns>The generated histogram.</returns>
    [Histogram<double>(typeof(ReportTags), Name = "jerrychart.api.statistics.query.duration.seconds")]
    public static partial QueryDuration CreateQueryDuration(Meter meter);

    /// <summary>Creates the report query error counter.</summary>
    /// <param name="meter">The application meter.</param>
    /// <returns>The generated counter.</returns>
    [Counter<long>(typeof(QueryErrorTags), Name = "jerrychart.api.statistics.query.errors")]
    public static partial QueryErrors CreateQueryErrors(Meter meter);

    /// <summary>Creates the list report size histogram.</summary>
    /// <param name="meter">The application meter.</param>
    /// <returns>The generated histogram.</returns>
    [Histogram<long>(typeof(ReportTags), Name = "jerrychart.api.statistics.result.items")]
    public static partial ResultItems CreateResultItems(Meter meter);

    /// <summary>Creates the sampled durable checkpoint age gauge.</summary>
    /// <param name="meter">The application meter.</param>
    /// <returns>The generated gauge.</returns>
#pragma warning disable EXTEXP0003 // The source generator's gauge attribute is experimental in 10.10.0.
    [Gauge<double>(Name = "jerrychart.api.statistics.data.age.seconds")]
#pragma warning restore EXTEXP0003
    public static partial DataAge CreateDataAge(Meter meter);
}

internal static class ApiMetrics
{
    internal const string MeterName = "JerryChart.Api";
    private static readonly Meter s_meter = new(MeterName);
    private static readonly QueryDuration s_duration = ApiMetricInstruments.CreateQueryDuration(s_meter);
    private static readonly QueryErrors s_errors = ApiMetricInstruments.CreateQueryErrors(s_meter);
    private static readonly ResultItems s_items = ApiMetricInstruments.CreateResultItems(s_meter);
    private static readonly DataAge s_age = ApiMetricInstruments.CreateDataAge(s_meter);

    internal static MeterProviderBuilder AddApiMetrics(this MeterProviderBuilder builder)
    {
        return builder.AddMeter(MeterName)
            .AddView("jerrychart.api.statistics.query.duration.seconds", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 15, 30]
            })
            .AddView("jerrychart.api.statistics.result.items", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = [0, 1, 5, 10, 50, 100, 500, 1000, 10000]
            });
    }

    internal static async Task<T> QueryAsync<T>(StatisticsReport report, Func<Task<T>> query,
        CancellationToken cancellationToken, Func<T, int>? count = null)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            T result = await query();
            if (count is not null)
            {
                s_items.Record(count(result), new ReportTags { Report = report });
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            QueryError category = exception switch
            {
                MySqlException => QueryError.Database,
                TimeoutException => QueryError.Timeout,
                OperationCanceledException => QueryError.Cancellation,
                _ => QueryError.Other
            };
            s_errors.Add(1, new QueryErrorTags { Report = report, Error = category });
            throw;
        }
        finally
        {
            s_duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new ReportTags { Report = report });
        }
    }

    internal static void RecordDataAge(DateTimeOffset? checkpoint, DateTimeOffset now)
    {
        if (checkpoint is { } timestamp)
        {
            s_age.Record(Math.Max(0, (now - timestamp).TotalSeconds));
        }
    }
}
