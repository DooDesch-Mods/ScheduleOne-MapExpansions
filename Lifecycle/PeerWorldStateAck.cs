namespace MapExpansions.Lifecycle
{
    /// <summary>
    /// One peer's position in the snapshot barrier. A peer that never confirmed a revision is still joining and is
    /// disconnected after <see cref="TimeoutSeconds"/>; after its first confirmation a new revision only holds its
    /// native replication until the matching acknowledgement arrives.
    /// </summary>
    internal sealed class PeerWorldStateAck
    {
        internal const double TimeoutSeconds = 25;
        internal const double RetrySeconds = 5;
        private double _nextRequest;
        internal long Revision { get; private set; } = -1;
        internal bool Confirmed { get; private set; }
        internal bool EverConfirmed { get; private set; }
        internal bool TimedOut { get; private set; }
        internal double Started { get; private set; }
        internal PeerWorldStateAck(double now) { Started = now; _nextRequest = now; }
        internal bool Require(long revision, double now)
        {
            if (revision < 0 || revision < Revision || TimedOut) return false;
            if (revision == Revision) return true;
            if (Confirmed) Started = now;
            Revision = revision;
            Confirmed = false;
            // A new revision means the consumer is sending it now, so the next request can wait a full interval.
            _nextRequest = Math.Max(_nextRequest, now + RetrySeconds);
            return true;
        }
        internal bool Confirm(long revision)
        {
            if (revision < 0 || revision != Revision || TimedOut) return false;
            Confirmed = true;
            EverConfirmed = true;
            return true;
        }
        internal bool Expire(double now)
        {
            if (Confirmed || EverConfirmed || TimedOut || now - Started < TimeoutSeconds) return false;
            TimedOut = true;
            return true;
        }
        /// <summary>True at most once per <see cref="RetrySeconds"/> while an acknowledgement is outstanding.</summary>
        internal bool ShouldRequest(double now)
        {
            if (Confirmed || TimedOut || now < _nextRequest) return false;
            _nextRequest = now + RetrySeconds;
            return true;
        }
    }
}
