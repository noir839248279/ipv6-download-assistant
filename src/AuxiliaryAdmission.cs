namespace IPv6Lens;

// Keep stalled control requests from occupying every content relay slot.
internal sealed class AuxiliaryAdmission(IEnumerable<string> hosts, Func<long>? clock = null)
{
    internal const int PerHostLimit = 8, TotalLimit = 32, CooldownSeconds = 15;
    readonly object sync = new();
    readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    readonly Dictionary<string, Entry> entries = hosts.Distinct(StringComparer.OrdinalIgnoreCase)
        .ToDictionary(h => h, _ => new Entry(), StringComparer.OrdinalIgnoreCase);
    int active;
    internal sealed class Entry
    {
        internal int Active, Failures;
        internal long Until, Rejected, CooldownRejected, HostLimitRejected, TotalLimitRejected;
        internal string? LastReason;
    }
    internal sealed class Lease(AuxiliaryAdmission owner, Entry entry) : IDisposable
    {
        bool disposed;
        internal void Connected()
        {
            lock (owner.sync)
                if (!disposed && owner.now() >= entry.Until) entry.Failures = 0;
        }
        internal void ConnectFailed()
        {
            lock (owner.sync)
            {
                if (disposed) return;
                if (++entry.Failures >= 3 && owner.now() >= entry.Until)
                {
                    entry.Until = owner.now() + CooldownSeconds * 1000L;
                    entry.Failures = 0;
                }
            }
        }
        public void Dispose()
        {
            lock (owner.sync)
            {
                if (disposed) return;
                disposed = true;
                entry.Active--; owner.active--;
            }
        }
    }
    internal bool TryEnter(string host, out Lease? lease, out int retrySeconds)
    {
        lock (sync)
        {
            lease = null; retrySeconds = 1;
            var entry = entries[host];
            long remaining = entry.Until - now();
            if (remaining > 0 || entry.Active >= PerHostLimit || active >= TotalLimit)
            {
                entry.Rejected++;
                if (remaining > 0)
                {
                    entry.CooldownRejected++; entry.LastReason = "cooldown";
                    retrySeconds = (int)Math.Ceiling(remaining / 1000d);
                }
                else if (entry.Active >= PerHostLimit)
                { entry.HostLimitRejected++; entry.LastReason = "host_limit"; }
                else
                { entry.TotalLimitRejected++; entry.LastReason = "total_limit"; }
                return false;
            }
            entry.Active++; active++;
            lease = new Lease(this, entry);
            return true;
        }
    }
    internal object Snapshot()
    {
        lock (sync)
            return new
            {
                active, totalLimit = TotalLimit, perHostLimit = PerHostLimit,
                hosts = entries.Where(p => p.Value.Active > 0 || p.Value.Rejected > 0 || p.Value.Until > now())
                    .Select(p => new { host = p.Key, active = p.Value.Active, rejected = p.Value.Rejected,
                        cooldownRejected = p.Value.CooldownRejected, hostLimitRejected = p.Value.HostLimitRejected,
                        totalLimitRejected = p.Value.TotalLimitRejected, lastReason = p.Value.LastReason,
                        retrySeconds = Math.Max(0, (long)Math.Ceiling((p.Value.Until - now()) / 1000d)) }).ToArray()
            };
    }
}
