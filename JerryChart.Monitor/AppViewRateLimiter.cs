// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Net;

namespace JerryChart.Monitor;

internal sealed class AppViewRateLimiter(
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay =
        wait ?? ((duration, token) => Task.Delay(duration, token));
    private DateTimeOffset _nextRequestAt;
    private DateTimeOffset _serverCooldownUntil;

    internal TimeSpan ServerCooldownRemaining
    {
        get
        {
            TimeSpan remaining = _serverCooldownUntil - _clock.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    internal async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        await WaitForNextRequestAsync(cancellationToken);
        _nextRequestAt = _clock.GetUtcNow().AddSeconds(5);

        HttpResponseMessage response = await send(cancellationToken);
        DateTimeOffset now = _clock.GetUtcNow();
        _nextRequestAt = Max(_nextRequestAt, now.AddSeconds(5));
        TimeSpan serverDelay = ServerDelay(response, now);
        _nextRequestAt = Max(_nextRequestAt, now + serverDelay);
        _serverCooldownUntil = Max(_serverCooldownUntil, now + serverDelay);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _nextRequestAt = Max(_nextRequestAt, now.AddMinutes(5));
            _serverCooldownUntil = Max(_serverCooldownUntil, now.AddMinutes(5));
        }

        return response;
    }

    private async Task WaitForNextRequestAsync(CancellationToken cancellationToken)
    {
        TimeSpan remaining = _nextRequestAt - _clock.GetUtcNow();
        while (remaining > TimeSpan.Zero)
        {
            await _delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, cancellationToken);
            remaining = _nextRequestAt - _clock.GetUtcNow();
        }
    }

    private static TimeSpan ServerDelay(HttpResponseMessage response, DateTimeOffset now)
    {
        TimeSpan duration = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - now : TimeSpan.Zero);
        bool exhausted = response.StatusCode == HttpStatusCode.TooManyRequests ||
            (response.Headers.TryGetValues("RateLimit-Remaining", out IEnumerable<string>? values) &&
            long.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out long remaining) && remaining <= 0);

        if (exhausted &&
            response.Headers.TryGetValues("RateLimit-Reset", out IEnumerable<string>? resets) &&
            long.TryParse(resets.FirstOrDefault(), CultureInfo.InvariantCulture, out long reset) &&
            reset is >= 0 and <= 253402300799)
        {
            TimeSpan resetDelay = DateTimeOffset.FromUnixTimeSeconds(reset) - now;
            duration = resetDelay > duration ? resetDelay : duration;
        }

        return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
    {
        return left > right ? left : right;
    }
}