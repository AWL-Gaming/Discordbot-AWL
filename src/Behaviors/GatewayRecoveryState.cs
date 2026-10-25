namespace DiscordBot;

// Tracks gateway state across WebSocket replacements without keeping a failed heartbeat alive.
internal sealed class GatewayRecoveryState
{
    internal bool HeartbeatAcknowledged { get; private set; } = true;
    internal bool ReconnectPending { get; private set; }
    internal string? SessionId { get; private set; }
    internal string? ResumeGatewayUrl { get; private set; }
    internal int? LastSequence { get; private set; }
    internal bool CanResume => !string.IsNullOrEmpty(SessionId) && LastSequence.HasValue;

    internal void OnHello() => HeartbeatAcknowledged = true;
    internal void OnHeartbeatSent() => HeartbeatAcknowledged = false;
    internal void OnHeartbeatAck() => HeartbeatAcknowledged = true;

    internal void OnDispatchSequence(int? sequence)
    {
        if (sequence.HasValue) LastSequence = sequence.Value;
    }

    internal void OnReady(string? sessionId, string? resumeGatewayUrl)
    {
        SessionId = sessionId;
        ResumeGatewayUrl = resumeGatewayUrl;
    }

    internal void InvalidateSession()
    {
        SessionId = null;
        ResumeGatewayUrl = null;
        LastSequence = null;
    }

    internal bool TryBeginReconnect(bool invalidateSession = false)
    {
        if (invalidateSession) InvalidateSession();
        if (ReconnectPending) return false;
        ReconnectPending = true;
        OnHello();
        return true;
    }

    internal void EndReconnect() => ReconnectPending = false;

    internal void ResetForTokenChange()
    {
        InvalidateSession();
        EndReconnect();
        OnHello();
    }
}
