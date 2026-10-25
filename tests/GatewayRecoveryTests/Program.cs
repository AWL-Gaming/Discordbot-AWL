using DiscordBot;

var checks = 0;
void Check(bool actual, string description)
{
    if (!actual) throw new Exception("FAIL: " + description);
    checks++;
}

var state = new GatewayRecoveryState();
Check(state.HeartbeatAcknowledged, "initial heartbeat ready");
Check(!state.CanResume, "no prior session on fresh start");
state.OnHello();
state.OnHeartbeatSent();
Check(!state.HeartbeatAcknowledged, "sent heartbeat requires acknowledgment");
state.OnHeartbeatAck();
Check(state.HeartbeatAcknowledged, "ACK clears outstanding heartbeat");

state.OnDispatchSequence(42);
state.OnReady("session-42", "wss://resume.example.invalid");
Check(state.CanResume, "READY allows resumption");
Check(state.LastSequence == 42, "sequence preserved");
state.OnHeartbeatSent();
Check(!state.HeartbeatAcknowledged, "missed ACK detected");
Check(state.TryBeginReconnect(), "first failed heartbeat starts recovery");
Check(!state.TryBeginReconnect(), "duplicate reconnect suppressed");
Check(state.ReconnectPending, "reconnect remains pending");
state.EndReconnect();
state.OnHello();
Check(state.HeartbeatAcknowledged, "new HELLO resets missed heartbeat");
Check(state.CanResume, "new connection retains session to resume");
Check(state.SessionId == "session-42" && state.LastSequence == 42, "resume payload retains session and sequence");
state.OnHeartbeatSent();
state.OnHeartbeatAck();
Check(state.HeartbeatAcknowledged, "resumed connection receives next ACK");

Check(state.TryBeginReconnect(false), "resumable INVALID_SESSION reconnects once");
Check(state.CanResume, "resumable invalid session keeps session");
Check(!state.TryBeginReconnect(false), "duplicate resumable invalid session ignored");
state.EndReconnect();
Check(state.TryBeginReconnect(true), "non-resumable invalid session reconnects");
Check(!state.CanResume && state.SessionId == null && state.LastSequence == null && state.ResumeGatewayUrl == null, "non-resumable session cleared");
Check(!state.TryBeginReconnect(), "repeated invalid session does not spawn another reconnect");
state.EndReconnect();
state.OnHello();
Check(state.HeartbeatAcknowledged, "fresh identify starts with clean heartbeat");
state.OnDispatchSequence(null);
Check(state.LastSequence == null, "no sequence on fresh identify");
state.OnDispatchSequence(109);
state.OnDispatchSequence(null);
Check(state.LastSequence == 109, "null event sequence does not overwrite dispatch sequence");

state.ResetForTokenChange();
Check(!state.CanResume && !state.ReconnectPending && state.HeartbeatAcknowledged, "token change clears session, reconnect and heartbeat");

for (var i = 0; i < 100; i++)
{
    state.OnHeartbeatSent();
    Check(!state.HeartbeatAcknowledged, "failure detected on cycle " + i);
    Check(state.TryBeginReconnect(), "one reconnect on cycle " + i);
    for (var duplicate = 0; duplicate < 4; duplicate++)
        Check(!state.TryBeginReconnect(), "duplicate blocked on cycle " + i);
    state.EndReconnect();
    state.OnHello();
    Check(state.HeartbeatAcknowledged, "recovered HELLO on cycle " + i);
    state.OnHeartbeatSent();
    state.OnHeartbeatAck();
    Check(state.HeartbeatAcknowledged, "recovered ACK on cycle " + i);
}
Console.WriteLine($"PASS: {checks} gateway recovery assertions (ACK loss, resume, invalid session, duplicates and 100 repeated failures).");
