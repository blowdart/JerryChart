// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using JerryChart.Monitor;

namespace JerryChart.Tests;

/// <summary>Verifies named statuses preserve the existing database values.</summary>
[TestClass]
public sealed class ParentUriBackfillStatusTests
{
    /// <summary>Verifies the persisted byte value for each named status.</summary>
    /// <param name="name">The status name.</param>
    /// <param name="expected">The existing database value.</param>
    [TestMethod]
    [DataRow("Pending", (byte)0)]
    [DataRow("Resolved", (byte)1)]
    [DataRow("Unavailable", (byte)2)]
    [DataRow("RetryPending", (byte)3)]
    public void StatusValuesRemainCompatible(string name, byte expected)
    {
        ParentUriBackfillStatus status = Enum.Parse<ParentUriBackfillStatus>(name);
        Assert.AreEqual(expected, (byte)status);
    }
}