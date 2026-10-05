// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

using JerryChart.Data;

using Microsoft.AspNetCore.Mvc;

namespace JerryChart.Api;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ReplySummary))]
[JsonSerializable(typeof(StatisticsLastUpdated))]
[JsonSerializable(typeof(ProcessingStatus))]
[JsonSerializable(typeof(IReadOnlyList<TopReplyAuthor>))]
[JsonSerializable(typeof(IReadOnlyList<TopReplyPost>))]
[JsonSerializable(typeof(IReadOnlyList<MonthlyReplyCount>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
