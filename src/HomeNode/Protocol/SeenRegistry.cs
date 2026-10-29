namespace HomeNode.Protocol;

/// <summary>
/// Replay guard for one endpoint. A packet is admitted only if
/// neither its packet_id nor its (connection_id, packet_nonce)
/// pair has been seen. Admission is recorded before opening, so a
/// replayed packet is rejected even if the first copy is still
/// being processed. Endpoints keep the registry for at least the
/// session lifetime plus the relay queue window; the class itself
/// is in memory and thread safe.
/// </summary>
public sealed class SeenRegistry
{
    private readonly object _lock = new();
    private readonly HashSet<string> _packetIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nonces = new(StringComparer.Ordinal);

    /// <summary>
    /// Envelope level step, run before opening: rejects a packet_id
    /// that has been seen and records this one.
    /// </summary>
    public void CheckAndRecordEnvelope(Envelope envelope)
    {
        lock (_lock)
        {
            if (!_packetIds.Add(envelope.PacketId))
                throw new ProtocolException("Replay rejected: packet_id already seen: " + envelope.PacketId);
        }
    }

    /// <summary>
    /// Plain level step, run after opening: rejects a packet nonce
    /// that has been used on this connection and records this one.
    /// A fresh packet_id carrying a reused nonce is still a replay.
    /// </summary>
    public void CheckAndRecordNonce(PlainPacket plain)
    {
        lock (_lock)
        {
            string nonceKey = plain.ConnectionId + "|" + plain.PacketNonce;
            if (!_nonces.Add(nonceKey))
                throw new ProtocolException("Replay rejected: packet nonce already used on this connection.");
        }
    }

    public void CheckAndRecord(Envelope envelope, PlainPacket plain)
    {
        CheckAndRecordEnvelope(envelope);
        CheckAndRecordNonce(plain);
    }

    /// <summary>
    /// Envelope level pre-check an endpoint can run before opening,
    /// so a replayed packet_id is rejected before extraction.
    /// </summary>
    public bool HasSeenPacketId(string packetId)
    {
        lock (_lock) return _packetIds.Contains(packetId);
    }
}
