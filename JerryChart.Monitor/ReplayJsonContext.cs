// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

using idunno.AtProto.Jetstream.Archive;

namespace JerryChart.Monitor;

[JsonSerializable(typeof(SnapshotCheckpoint))]
[JsonSerializable(typeof(MonitorProgress))]
internal sealed partial class ReplayJsonContext : JsonSerializerContext;