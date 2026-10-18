using System;

namespace Luma;

public sealed record ProfileFeedback(string Title, string Message, string Key);

public sealed class FeedbackGate
{
    private string? _lastKey;
    private DateTimeOffset _lastAt;
    public bool Accept(string key, DateTimeOffset now)
    {
        if (_lastKey == key && now >= _lastAt && now - _lastAt < TimeSpan.FromSeconds(2)) return false;
        _lastKey = key; _lastAt = now; return true;
    }
    public static string AutomaticTitle(bool gameActive, bool previouslyGaming) => gameActive ? "Oyun algılandı"
        : previouslyGaming ? "Oyun kapandı" : "Saat profili uygulandı";
}
