using System.Text.Json.Serialization;

namespace OpenFreqServer.SrsBridge;

/// <summary>
/// Admin-editable settings for the SRS (DCS-SimpleRadioStandalone) protocol bridge. When enabled
/// (<see cref="ServerConfig.SrsBridgeEnabled"/>), real unmodified SRS client apps can connect to
/// this server and communicate with OpenFreq clients on shared frequencies -- see
/// docs/SRS_BRIDGE.md for the full architecture.
/// </summary>
public class SrsBridgeConfig
{
    /// <summary>TCP+UDP port SRS clients connect to. 5002 matches real SRS's own default so
    /// existing SRS client configs/shortcuts need no changes beyond pointing at this server's IP.</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = 5002;

    [JsonPropertyName("bindAddress")]
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>SRS has no concept of the ARC-210's PRST/DAMA login procedure -- it only tunes a
    /// numeric frequency. When true (default), a bridged SRS player on an OpenFreq SATCOM
    /// frequency is always treated as instantly logged in (bypassing the 5-second client-side
    /// acquisition timer real OpenFreq clients go through), since there's no equivalent SRS-side
    /// procedure to time. DAMA network-access/slot contention is never bypassed by this flag --
    /// that's a genuine shared resource, not a client-side procedure. GAMEPLAY_CONFIG.</summary>
    [JsonPropertyName("treatSatcomAsInstantLogin")]
    public bool TreatSatcomAsInstantLogin { get; set; } = true;

    /// <summary>SRS protocol/client version this bridge reports itself as (sent in every reply
    /// message) and the minimum client version it accepts. Real SRS clients may reject servers
    /// reporting an old version, or the bridge may reject genuinely ancient clients -- these are
    /// PROJECT_OBSERVED from the SRS 2.3.8.2 public release and will need bumping over time as SRS
    /// itself updates; see ciribob/DCS-SimpleRadioStandalone's Common/Helpers/UpdaterChecker.cs for
    /// the authoritative current values.</summary>
    [JsonPropertyName("reportedSrsVersion")]
    public string ReportedSrsVersion { get; set; } = "2.3.8.2";

    [JsonPropertyName("minimumClientVersion")]
    public string MinimumClientVersion { get; set; } = "1.9.0.0";

    /// <summary>External AWACS Mode -- lets an SRS client log in with a coalition password
    /// instead of a live DCS export, for GCI/AWACS-style controllers (or, usefully, for testing
    /// this bridge without DCS running at all). Off by default; set passwords and flip this on to
    /// enable it. Mirrors real SRS's EXTERNAL_AWACS_MODE/_BLUE_PASSWORD/_RED_PASSWORD server
    /// settings.</summary>
    [JsonPropertyName("externalAwacsModeEnabled")]
    public bool ExternalAwacsModeEnabled { get; set; } = false;

    [JsonPropertyName("externalAwacsModeBluePassword")]
    public string ExternalAwacsModeBluePassword { get; set; } = "";

    [JsonPropertyName("externalAwacsModeRedPassword")]
    public string ExternalAwacsModeRedPassword { get; set; } = "";

    public static SrsBridgeConfig Default => new();
}
