// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;

namespace JerryChart.Tests;

/// <summary>Verifies executable application assemblies embed the configured version.</summary>
[TestClass]
public sealed class ApplicationVersionTests
{
    /// <summary>Verifies the API, monitor, and AppHost use the configured 1.0 base version.</summary>
    [TestMethod]
    [DataRow("JerryChart.Api")]
    [DataRow("JerryChart.Monitor")]
    [DataRow("JerryChart.AppHost")]
    public void ExecutableAssembliesUseOnePointZeroBaseVersion(string assemblyName)
    {
        Assembly assembly = Assembly.Load(assemblyName);
        AssemblyInformationalVersionAttribute? version =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.IsNotNull(version);
        Assert.IsTrue(version.InformationalVersion.StartsWith("1.0.", StringComparison.Ordinal),
            $"{assemblyName} should report a version based on 1.0, but reported {version.InformationalVersion}.");
    }
}
