namespace MapExpansions.Lifecycle
{
    internal sealed class PeerWorldStateAck
    {
        internal const double TimeoutSeconds = 25;
        internal long Revision { get; private set; } = -1;
        internal bool Confirmed { get; private set; }
        internal bool TimedOut { get; private set; }
        internal double Started { get; private set; }
        internal PeerWorldStateAck(double now) { Started = now; }
        internal bool Require(long revision, double now)
        {
            if (revision < 0 || revision < Revision || TimedOut) return false;
            if (revision == Revision) return true;
            if (Confirmed) Started = now;
            Revision = revision;
            Confirmed = false;
            return true;
        }
        internal bool Confirm(long revision)
        {
            if (revision < 0 || revision != Revision || TimedOut) return false;
            Confirmed = true;
            return true;
        }
        internal bool Expire(double now)
        {
            if (Confirmed || TimedOut || now - Started < TimeoutSeconds) return false;
            TimedOut = true;
            return true;
        }
    }
}
