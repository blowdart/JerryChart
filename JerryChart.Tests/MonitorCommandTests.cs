// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using JerryChart.Monitor;

namespace JerryChart.Tests;

/// <summary>Verifies monitor command routing and parsing without external services.</summary>
[TestClass]
public sealed class MonitorCommandTests
{
    /// <summary>Verifies default and explicit monitoring.</summary>
    /// <param name="arguments">The command-line arguments.</param>
    /// <param name="expectedCommand">The expected handler.</param>
    /// <returns>A task representing the command test.</returns>
    [TestMethod]
    [DataRow("", "run")]
    [DataRow("run", "run")]
    [DataRow("backfill-parent-uris", "backfill-parent-uris")]
    public async Task RoutesCommands(string arguments, string expectedCommand)
    {
        string? invoked = null;
        var command = MonitorCommand.Create(
            _ =>
            {
                invoked = "run";
                return Task.FromResult(2);
            },
            _ =>
            {
                invoked = "backfill-parent-uris";
                return Task.FromResult(2);
            });

        var result = await command.Parse(arguments).InvokeAsync();

        Assert.AreEqual(expectedCommand, invoked);
        Assert.AreEqual(2, result);
    }

    /// <summary>Verifies help is available without starting the host or accessing services.</summary>
    /// <param name="arguments">The help arguments.</param>
    /// <returns>A task representing the help test.</returns>
    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    [DataRow("run --help")]
    [DataRow("backfill-parent-uris --help")]
    public async Task HelpDoesNotInvokeHandlers(string arguments)
    {
        var invoked = false;
        Task<int> Handler(CancellationToken _)
        {
            invoked = true;
            return Task.FromResult(0);
        }

        var result = await MonitorCommand.Create(Handler, Handler).Parse(arguments).InvokeAsync();

        Assert.AreEqual(0, result);
        Assert.IsFalse(invoked);
    }

    /// <summary>Verifies removed commands, unknown options, and extra arguments are rejected.</summary>
    /// <param name="arguments">The invalid arguments.</param>
    /// <returns>A task representing the parsing test.</returns>
    [TestMethod]
    [DataRow("add \"Hello from the command line\"")]
    [DataRow("unknown")]
    [DataRow("run extra")]
    [DataRow("list extra")]
    [DataRow("list")]
    [DataRow("--unknown")]
    public async Task InvalidArgumentsDoNotInvokeHandlers(string arguments)
    {
        var invoked = false;
        Task<int> Handler(CancellationToken _)
        {
            invoked = true;
            return Task.FromResult(0);
        }

        var parsed = MonitorCommand.Create(Handler).Parse(arguments);
        Assert.IsNotEmpty(parsed.Errors);

        var result = await parsed.InvokeAsync();

        Assert.AreEqual(1, result);
        Assert.IsFalse(invoked);
    }
}