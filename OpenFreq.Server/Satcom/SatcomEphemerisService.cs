using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenFreq.Common.Satcom;
using SGPdotNET.Propagation;
using SGPdotNET.TLE;

[assembly: InternalsVisibleTo("OpenFreq.Server.Tests")]

namespace OpenFreqServer.Satcom;

/// <summary>
/// Resolves each catalog satellite's current position, once per <see cref="SatcomServerConfig.PropagationHz"/>
/// tick, on a background timer (never on any request/audio path). Two first-class modes:
///
/// - StaticGeo: fixed longitude/altitude, no time dependence, no network.
/// - LiveTle: real orbital position. Elements are fetched from CelesTrak by
///   <see cref="CachingRemoteTleProvider"/> (a vetted part of the SGP.NET library, not hand-rolled --
///   it owns the HTTPS fetch, on-disk caching, and max-age refresh logic) and propagated with
///   SGP.NET's <see cref="Sgp4"/> (a vetted SGP4 implementation, not a hand-rolled propagator).
///   SGP.NET's own EciCoordinate.ToGeodetic() performs the TEME-&gt;geodetic conversion (nutation/
///   sidereal-time-aware), which is preferred here over this project's own SatcomOrbitMath for the
///   live path -- SatcomOrbitMath remains available (and tested) as a simpler, documented utility,
///   not the primary conversion for real ephemeris.
///
/// Never crashes or hard-fails without internet: a fetch/propagation failure marks the satellite's
/// last-known position stale and, once no cached position exists at all, falls back to the
/// satellite definition's own Static* fields. All work here (HTTP, SGP4, JSON/TLE parsing) is
/// strictly background-timer-only; consumers read a small precomputed position snapshot from a
/// lock-free ConcurrentDictionary.
/// </summary>
public sealed class SatcomEphemerisService : IDisposable
{
    private readonly IReadOnlyList<SatcomSatelliteDefinition> _catalog;
    private readonly SatcomServerConfig _config;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SatcomSatellitePosition> _positions = new();
    private readonly Dictionary<string, CachingRemoteTleProvider> _tleProviders = new();
    private readonly Dictionary<string, DateTime> _lastGoodPropagationUtc = new();
    private readonly Dictionary<string, DateTime> _lastFailedFetchUtc = new();
    private Timer? _timer;

    /// <summary>CachingRemoteTleProvider.GetTle re-attempts a live network fetch on every call
    /// once its own on-disk-cache-freshness window (MaxAge) has passed, and never advances its
    /// internal LastRefresh on failure -- so with no throttling here, an unreachable CelesTrak
    /// would otherwise be retried (and logged) on literally every propagation tick forever. See
    /// PropagateLiveTle.</summary>
    private static readonly TimeSpan FailedFetchRetryBackoff = TimeSpan.FromSeconds(30);

    public SatcomEphemerisService(IReadOnlyList<SatcomSatelliteDefinition> catalog, SatcomServerConfig config,
        ILogger logger)
    {
        _catalog = catalog;
        _config = config;
        _logger = logger;

        Directory.CreateDirectory(ResolveCacheDirectory());

        foreach (var sat in _catalog)
        {
            if (sat.EphemerisMode != SatcomEphemerisMode.LiveTle || sat.NoradId is not { } noradId)
                continue;

            try
            {
                // CATNR/FORMAT=TLE is CelesTrak's stable per-satellite element endpoint; three-line
                // format includes the object name line SGP.NET's Tle type expects.
                var uri = new Uri($"https://celestrak.org/NORAD/elements/gp.php?CATNR={noradId}&FORMAT=TLE");
                var cacheFile = Path.Combine(ResolveCacheDirectory(), $"sat_{noradId}.tle");
                _tleProviders[sat.Id] = new CachingRemoteTleProvider(
                    threeLine: true,
                    maxAge: TimeSpan.FromHours(Math.Max(0.1, _config.EphemerisFetchIntervalHours)),
                    localFilename: cacheFile,
                    sources: [uri]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SATCOM: failed to set up LiveTle provider for {SatelliteId} (NORAD {NoradId})",
                    sat.Id, noradId);
            }
        }
    }

    private string ResolveCacheDirectory() =>
        Path.IsPathRooted(_config.EphemerisCacheDirectory)
            ? _config.EphemerisCacheDirectory
            : Path.Combine(AppContext.BaseDirectory, _config.EphemerisCacheDirectory);

    public void Start()
    {
        var periodMs = (int)Math.Max(50.0, 1000.0 / Math.Max(0.1, _config.PropagationHz));
        _timer = new Timer(_ => SafePropagateAll(), null, dueTime: 0, period: periodMs);
    }

    private void SafePropagateAll()
    {
        try { PropagateAll(); }
        catch (Exception ex) { _logger.LogError(ex, "SATCOM: unhandled error during ephemeris propagation tick"); }
    }

    /// <summary>Runs one propagation pass synchronously -- public so tests can drive deterministic
    /// StaticGeo/LiveTle-with-injected-cache scenarios without waiting on the background timer.</summary>
    public void PropagateAll()
    {
        var nowUtc = DateTime.UtcNow;
        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var sat in _catalog)
        {
            if (!sat.Enabled) continue;

            if (sat.EphemerisMode == SatcomEphemerisMode.StaticGeo)
            {
                _positions[sat.Id] = new SatcomSatellitePosition(
                    sat.Id, 0.0, sat.StaticLongitudeDeg, sat.StaticAltitudeMeters, nowUnixMs, IsStale: false);
                continue;
            }

            PropagateLiveTle(sat, nowUtc, nowUnixMs);
        }
    }

    private void PropagateLiveTle(SatcomSatelliteDefinition sat, DateTime nowUtc, long nowUnixMs)
    {
        if (sat.NoradId is not { } noradId || !_tleProviders.TryGetValue(sat.Id, out var provider))
        {
            FallBackToStatic(sat, nowUnixMs, "no NORAD id / provider configured");
            return;
        }

        if (_lastFailedFetchUtc.TryGetValue(sat.Id, out var lastFailure) &&
            nowUtc - lastFailure < FailedFetchRetryBackoff)
        {
            // Still backing off after a recent fetch failure -- whatever _positions already
            // holds (last-known-good marked stale, or the disk-cache fallback below) stays as-is
            // until the backoff elapses, instead of hammering CelesTrak and this log every tick.
            return;
        }

        try
        {
            var tle = provider.GetTle(noradId);
            var eci = new Sgp4(tle).FindPosition(nowUtc);
            var geo = eci.ToGeodetic();

            _positions[sat.Id] = new SatcomSatellitePosition(
                sat.Id, geo.Latitude.Degrees, geo.Longitude.Degrees, geo.Altitude * 1000.0,
                nowUnixMs, IsStale: false);
            _lastGoodPropagationUtc[sat.Id] = nowUtc;
            _lastFailedFetchUtc.Remove(sat.Id);
        }
        catch (Exception ex)
        {
            _lastFailedFetchUtc[sat.Id] = nowUtc;

            var lastGood = _lastGoodPropagationUtc.GetValueOrDefault(sat.Id, DateTime.MinValue);
            var staleFor = nowUtc - lastGood;

            if (_positions.TryGetValue(sat.Id, out var lastPosition) && staleFor < TimeSpan.FromHours(24))
            {
                // Keep the last known good position (marked stale) rather than snapping to a
                // fallback -- a satellite that briefly failed to fetch/propagate shouldn't visibly
                // teleport for a transient CelesTrak hiccup.
                _positions[sat.Id] = lastPosition with { IsStale = true };
                _logger.LogWarning(ex, "SATCOM: LiveTle propagation failed for {SatelliteId}, using last-known position (stale {StaleFor})",
                    sat.Id, staleFor);
            }
            else if (TryPropagateFromDiskCache(sat, noradId, nowUtc, nowUnixMs))
            {
                // No in-memory position yet (e.g. server just (re)started) and the network fetch
                // failed -- CachingRemoteTleProvider only reads its own on-disk cache file when
                // it's within EphemerisFetchIntervalHours (see SGP.NET's
                // CachingRemoteTleProvider.FetchNewTles); once that window passes it always
                // attempts a live fetch and throws on failure with no disk fallback of its own,
                // even though the cached elements (whatever their age) are almost always a far
                // better estimate than the satellite definition's placeholder Static* fields. So
                // we read the same file ourselves here, independent of that freshness window.
                _logger.LogWarning(ex, "SATCOM: LiveTle propagation failed for {SatelliteId} with no in-memory position yet; " +
                    "using the on-disk TLE cache instead of a static fallback", sat.Id);
            }
            else
            {
                FallBackToStatic(sat, nowUnixMs, $"propagation failed and no usable cached position ({ex.Message})");
            }
        }
    }

    /// <summary>Reads and propagates from CachingRemoteTleProvider's own on-disk cache file
    /// directly, bypassing its MaxAge freshness check -- see the doc comment at its call site in
    /// PropagateLiveTle. Returns false (does not touch _positions) if the file is missing,
    /// unparseable, or doesn't contain this satellite's NORAD id.</summary>
    internal bool TryPropagateFromDiskCache(SatcomSatelliteDefinition sat, int noradId, DateTime nowUtc, long nowUnixMs)
    {
        var cacheFile = Path.Combine(ResolveCacheDirectory(), $"sat_{noradId}.tle");
        if (!File.Exists(cacheFile)) return false;

        try
        {
            // Line 0 is CachingRemoteTleProvider's own fetch timestamp (see its
            // WriteOutNewTles) -- skip it and parse the remaining 3-line-per-satellite TLE block
            // the same way its base RemoteTleProvider.PopulateTleTable does.
            var lines = File.ReadAllLines(cacheFile).Skip(1).ToArray();
            var tle = Tle.ParseElements(lines, threeLine: true)
                .FirstOrDefault(t => (int)t.NoradNumber == noradId);
            if (tle == null) return false;

            var eci = new Sgp4(tle).FindPosition(nowUtc);
            var geo = eci.ToGeodetic();

            _positions[sat.Id] = new SatcomSatellitePosition(
                sat.Id, geo.Latitude.Degrees, geo.Longitude.Degrees, geo.Altitude * 1000.0,
                nowUnixMs, IsStale: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SATCOM: failed to parse on-disk TLE cache for {SatelliteId}", sat.Id);
            return false;
        }
    }

    private void FallBackToStatic(SatcomSatelliteDefinition sat, long nowUnixMs, string reason)
    {
        _logger.LogWarning("SATCOM: {SatelliteId} falling back to static position ({Reason})", sat.Id, reason);
        _positions[sat.Id] = new SatcomSatellitePosition(
            sat.Id, 0.0, sat.StaticLongitudeDeg, sat.StaticAltitudeMeters, nowUnixMs, IsStale: true);
    }

    public SatcomSatellitePosition? GetPosition(string satelliteId) =>
        _positions.TryGetValue(satelliteId, out var p) ? p : null;

    public IReadOnlyCollection<SatcomSatellitePosition> GetAllPositions() => _positions.Values.ToArray();

    public void Dispose() => _timer?.Dispose();
}
