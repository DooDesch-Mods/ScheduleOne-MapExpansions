namespace MapExpansions.Lifecycle
{
    /// <summary>
    /// One peer's position in the snapshot barrier. A peer that never confirmed a revision is still joining and is
    /// disconnected after <see cref="TimeoutSeconds"/> without word from it; a progress report (the snapshot arrived and
    /// is being applied) restarts that wait, up to <see cref="JoinCapSeconds"/> after the join began. After its first
    /// confirmation a new revision only holds its native replication until the matching acknowledgement arrives.
    /// </summary>
    internal sealed class PeerWorldStateAck
    {
        internal const double TimeoutSeconds = 25;
        internal const double RetrySeconds = 5;
        // Applying a finished expansion on a loaded client took 18 s on the main thread; a peer that keeps reporting
        // progress gets this long in total before the join is given up.
        internal const double JoinCapSeconds = 180;
        private double _nextRequest;
        internal long Revision { get; private set; } = -1;
        internal bool Confirmed { get; private set; }
        internal bool EverConfirmed { get; private set; }
        internal bool TimedOut { get; private set; }
        internal double Started { get; private set; }
        /// <summary>When the peer last reported it is applying <see cref="Revision"/>; negative before any report.</summary>
        internal double LastProgress { get; private set; } = -1;
        internal PeerWorldStateAck(double now) { Started = now; _nextRequest = now; }
        internal bool Require(long revision, double now)
        {
            if (revision < 0 || revision < Revision || TimedOut) return false;
            if (revision == Revision) return true;
            if (Confirmed) Started = now;
            Revision = revision;
            LastProgress = -1;
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
        /// <summary>The peer holds this revision and is still applying it. False for any other revision.</summary>
        internal bool Progress(long revision, double now)
        {
            if (revision < 0 || revision != Revision || Confirmed || TimedOut) return false;
            LastProgress = now;
            // It already has the snapshot, so a repeat request would only make it apply the same state again.
            _nextRequest = Math.Max(_nextRequest, now + RetrySeconds);
            return true;
        }
        internal bool Expire(double now)
        {
            if (Confirmed || EverConfirmed || TimedOut) return false;
            double quietSince = Math.Max(Started, LastProgress);
            if (now - quietSince < TimeoutSeconds && now - Started < JoinCapSeconds) return false;
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
