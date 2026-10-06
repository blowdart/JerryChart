// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.AtProto;

namespace JerryChart.Monitor;

internal sealed record ActorResolution(Handle? Handle, string? AccountStatus)
{
    // Inactivity is not a failed handle lookup. Recheck daily because accounts can reactivate and
    // relay decisions can change; temporary sync/throttle problems and unknown profiles retry sooner.
    internal int RefreshSeconds => Handle is not null ||
        AccountStatus is "deleted" or "deactivated" or "suspended" or "takendown" or "inactive" ? 86400 : 900;
}
