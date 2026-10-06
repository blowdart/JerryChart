// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using JerryChart.Monitor;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace JerryChart.Tests;

/// <summary>Verifies shared monitor configuration and explicit baseline file-provider ownership.</summary>
[TestClass]
[DoNotParallelize]
public sealed class MonitorHostBuilderTests
{
    /// <summary>Verifies application-directory defaults survive an external working directory.</summary>
    [TestMethod]
    public void ExternalWorkingDirectoryRetainsBaselineAndDoesNotStartWorkers()
    {
        string root = CreateRoot();
        string originalDirectory = Environment.CurrentDirectory;
        try
        {
            Directory.SetCurrentDirectory(root);
            using var configuration = new MonitorHostBuilder();
            Assert.AreEqual("wss://jetstream.us-west.bsky.network",
                configuration.Builder.Configuration["Jetstream:Host"]);
            using IHost host = configuration.Builder.Build();
            Assert.DoesNotContain(service => service is ActorHandleUpdater, host.Services.GetServices<IHostedService>(),
                "Backfill configuration must not register the actor updater.");
            Assert.IsNull(host.Services.GetService<ScheduledParentUriBackfill>(),
                "The manual backfill command must not register or start the daily scheduler.");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(root, true);
        }
    }

    /// <summary>Verifies baseline settings have lower priority than working-directory, environment, and CLI settings.</summary>
    [TestMethod]
    public void ConfigurationPriorityAndProviderDisposal()
    {
        string root = CreateRoot();
        string baseline = Path.Combine(root, "baseline");
        Directory.CreateDirectory(baseline);
        File.WriteAllText(Path.Combine(baseline, "appsettings.json"),
            """{"PolishTest":{"Value":"baseline","Retained":"yes"}}""");
        File.WriteAllText(Path.Combine(root, "appsettings.json"),
            """{"PolishTest":{"Value":"working-directory"}}""");
        const string variable = "PolishTest__Value";
        string? original = Environment.GetEnvironmentVariable(variable);
        var files = new OwnedFiles(baseline);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            using (var configuration = new MonitorHostBuilder(contentRootPath: root, baselineFiles: files))
            {
                ConfigurationManager settings = configuration.Builder.Configuration;
                Assert.AreEqual("working-directory", settings["PolishTest:Value"]);
                Assert.AreEqual("yes", settings["PolishTest:Retained"]);
                Environment.SetEnvironmentVariable(variable, "environment");
                ((IConfigurationRoot)configuration.Builder.Configuration).Reload();
                Assert.AreEqual("environment", settings["PolishTest:Value"]);
                Assert.IsFalse(files.Disposed);
            }

            Assert.IsTrue(files.Disposed, "The explicit file provider must be disposed by its owner.");
            using var cli = new MonitorHostBuilder(
                ["--PolishTest:Value=command-line"], root, new PhysicalFileProvider(baseline));
            Assert.AreEqual("command-line", cli.Builder.Configuration["PolishTest:Value"]);
            Assert.AreEqual("yes", cli.Builder.Configuration["PolishTest:Retained"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
            Directory.Delete(root, true);
        }
    }

    /// <summary>Verifies configuration failures also release the explicitly owned file provider.</summary>
    [TestMethod]
    public void FailedConfigurationDisposesProvider()
    {
        string root = CreateRoot();
        var files = new OwnedFiles(root);
        try
        {
            Assert.ThrowsExactly<FileNotFoundException>(() =>
                new MonitorHostBuilder(contentRootPath: root, baselineFiles: files));
            Assert.IsTrue(files.Disposed);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Verifies the HTTP host reuses baseline configuration precedence without changing manual-command hosting.</summary>
    [TestMethod]
    public void WebHostRetainsBaselineAndHonorsExplicitContainerUrls()
    {
        string root = CreateRoot();
        string baseline = Path.Combine(root, "baseline");
        Directory.CreateDirectory(baseline);
        File.WriteAllText(Path.Combine(baseline, "appsettings.json"),
            """{"Jetstream":{"Host":"wss://baseline.invalid"},"Fixture":{"Value":"baseline","Retained":"yes"}}""");
        File.WriteAllText(Path.Combine(root, "appsettings.json"),
            """{"Fixture":{"Value":"working-directory"}}""");
        var files = new OwnedFiles(baseline);
        try
        {
            using (var configuration = new MonitorWebHostBuilder(
                ["--urls=http://0.0.0.0:9081", "--Fixture:Value=command-line"], root, files))
            {
                Assert.AreEqual("wss://baseline.invalid", configuration.Builder.Configuration["Jetstream:Host"]);
                Assert.AreEqual("command-line", configuration.Builder.Configuration["Fixture:Value"]);
                Assert.AreEqual("yes", configuration.Builder.Configuration["Fixture:Retained"]);
                Assert.AreEqual("http://0.0.0.0:9081", configuration.Builder.Configuration["urls"]);
                Assert.IsFalse(files.Disposed);
            }

            Assert.IsTrue(files.Disposed);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Verifies the loopback default and standard Docker URL/port environment settings without starting HTTP.</summary>
    [TestMethod]
    public void WebHostRespectsContainerUrlAndPortEnvironment()
    {
        string[] keys = ["ASPNETCORE_URLS", "DOTNET_URLS", "URLS", "ASPNETCORE_HTTP_PORTS",
            "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS", "HTTP_PORTS", "HTTPS_PORTS"];
        var original = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (string key in keys)
            {
                Environment.SetEnvironmentVariable(key, null);
            }

            using (var defaults = new MonitorWebHostBuilder())
            {
                Assert.AreEqual("http://localhost:8081", defaults.Builder.Configuration["urls"]);
            }

            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:9081");
            using (var urls = new MonitorWebHostBuilder())
            {
                Assert.AreEqual("http://0.0.0.0:9081", urls.Builder.Configuration["urls"]);
            }

            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", "9082");
            using var ports = new MonitorWebHostBuilder();
            Assert.IsTrue(string.IsNullOrWhiteSpace(ports.Builder.Configuration["urls"]),
                "The loopback fallback must not override an explicitly configured Docker HTTP port.");
            Assert.AreEqual("9082", ports.Builder.Configuration["HTTP_PORTS"]);
        }
        finally
        {
            foreach (string key in keys)
            {
                Environment.SetEnvironmentVariable(key, original[key]);
            }
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "TestResults", $"monitor-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        return root;
    }

    private sealed class OwnedFiles(string root) : IFileProvider, IDisposable
    {
        private readonly PhysicalFileProvider _files = new(root);

        internal bool Disposed { get; private set; }

        /// <inheritdoc />
        public IFileInfo GetFileInfo(string subpath) => _files.GetFileInfo(subpath);

        /// <inheritdoc />
        public IDirectoryContents GetDirectoryContents(string subpath) => _files.GetDirectoryContents(subpath);

        /// <inheritdoc />
        public IChangeToken Watch(string filter) => _files.Watch(filter);

        /// <inheritdoc />
        public void Dispose()
        {
            Disposed = true;
            _files.Dispose();
        }
    }
}