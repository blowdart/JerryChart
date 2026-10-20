// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace JerryChart.Tests;

/// <summary>Verifies timestamped console output for shared service hosts.</summary>
[TestClass]
public sealed class ConsoleLoggingTests
{
    /// <summary>Verifies console entries include UTC timestamps with millisecond precision.</summary>
    [TestMethod]
    public void ServiceDefaultsEmitUtcConsoleTimestamps()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        using IHost host = builder.Build();
        SimpleConsoleFormatterOptions options = host.Services
            .GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue;
        Assert.IsTrue(options.UseUtcTimestamp);
        Assert.AreEqual("yyyy-MM-dd HH:mm:ss.fff 'UTC' ", options.TimestampFormat);

        ConsoleFormatter formatter = host.Services.GetServices<ConsoleFormatter>()
            .Single(formatter => formatter.Name == ConsoleFormatterNames.Simple);
        var entry = new LogEntry<string>(LogLevel.Information, "JerryChart.Tests", new EventId(42),
            "Timestamp fixture.", null, (state, _) => state);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        formatter.Write(entry, null, output);

        Assert.IsTrue(Regex.IsMatch(output.ToString(),
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} UTC info: JerryChart\.Tests\[42\]"),
            $"Unexpected console output: {output}");
        Assert.Contains("Timestamp fixture.", output.ToString());
    }
}
