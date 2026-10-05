// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace JerryChart.Monitor;

internal sealed class MonitorHostBuilder : IDisposable
{
    private readonly IFileProvider _baselineFiles;

    internal MonitorHostBuilder(
        string[]? args = null,
        string? contentRootPath = null,
        IFileProvider? baselineFiles = null)
    {
        _baselineFiles = baselineFiles ?? new PhysicalFileProvider(AppContext.BaseDirectory);
        try
        {
            Builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = contentRootPath
            });
            AddBaseline(Builder.Configuration, _baselineFiles);
            Builder.AddServiceDefaults();
        }
        catch
        {
            (Builder?.Configuration as IDisposable)?.Dispose();
            (_baselineFiles as IDisposable)?.Dispose();
            throw;
        }
    }

    internal HostApplicationBuilder Builder { get; }

    internal static void AddBaseline(ConfigurationManager configuration, IFileProvider files)
    {
        configuration.Sources.Insert(0, new JsonConfigurationSource
        {
            FileProvider = files,
            Path = "appsettings.json",
            Optional = false
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Hosts own their services; this scope owns the explicitly supplied file provider.
        (Builder.Configuration as IDisposable)?.Dispose();
        (_baselineFiles as IDisposable)?.Dispose();
    }
}