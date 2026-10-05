// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JerryChart.Data;

/// <summary>Registers the shared Aspire MySQL integration.</summary>
public static class DatabaseExtensions
{
    /// <summary>Adds a connection pool, health checks, telemetry, and the statistics store.</summary>
    /// <typeparam name="TBuilder">The host application builder type.</typeparam>
    /// <param name="builder">The builder to configure.</param>
    /// <returns>The configured builder.</returns>
    public static TBuilder AddJerryChartDatabase<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.AddMySqlDataSource("jerrychart");
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<StatisticsStore>();
        builder.Services.AddSingleton<ProcessingStatusStore>();

        return builder;
    }
}