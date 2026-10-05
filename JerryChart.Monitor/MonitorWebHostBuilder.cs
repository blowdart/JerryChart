// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace JerryChart.Monitor;

internal sealed class MonitorWebHostBuilder : IDisposable
{
    private readonly IFileProvider _baselineFiles;

    internal MonitorWebHostBuilder(string[]? args = null, string? contentRootPath = null,
        IFileProvider? baselineFiles = null)
    {
        _baselineFiles = baselineFiles ?? new PhysicalFileProvider(AppContext.BaseDirectory);
        try
        {
            Builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRootPath,
                ApplicationName = typeof(MonitorWebHostBuilder).Assembly.GetName().Name
            });
            MonitorHostBuilder.AddBaseline(Builder.Configuration, _baselineFiles);
            if (string.IsNullOrWhiteSpace(Builder.Configuration["urls"]) &&
                string.IsNullOrWhiteSpace(Builder.Configuration["HTTP_PORTS"]) &&
                string.IsNullOrWhiteSpace(Builder.Configuration["HTTPS_PORTS"]))
            {
                Builder.Configuration["urls"] = "http://localhost:8081";
            }

            Builder.AddServiceDefaults();
        }
        catch
        {
            (Builder?.Configuration as IDisposable)?.Dispose();
            (_baselineFiles as IDisposable)?.Dispose();
            throw;
        }
    }

    internal WebApplicationBuilder Builder { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        (Builder.Configuration as IDisposable)?.Dispose();
        (_baselineFiles as IDisposable)?.Dispose();
    }
}