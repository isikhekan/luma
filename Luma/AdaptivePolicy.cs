using System;

namespace Luma;

public sealed record AdaptiveDecision(string Scene, string Reason, bool GameActive)
{
    public string Context => Scene + (GameActive ? ":game" : ":clock");
}

// Keep Gaming through short process handovers and Alt+Tab; use the current clock on exit.
public sealed class AdaptivePolicy
{
    public static readonly TimeSpan ExitDelay = TimeSpan.FromSeconds(8);
    private DateTimeOffset? _lastGame;
    public AdaptiveDecision Observe(DateTimeOffset now, bool gameRunning, TimeOnly day, TimeOnly evening)
    {
        if (gameRunning) _lastGame = now;
        bool gaming = gameRunning || (_lastGame.HasValue && now >= _lastGame && now - _lastGame < ExitDelay);
        if (gaming) return new("gaming", gameRunning ? "game" : "game-exit-delay", true);
        bool daytime = TimeOnly.FromDateTime(now.DateTime).IsBetween(day, evening);
        return new(daytime ? "gaming" : "evening", daytime ? "day" : "evening", false);
    }
}
