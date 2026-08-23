namespace OpenFreqServer.SrsBridge;

/// <summary>
/// Server-settings dictionary sent to SRS clients in every SYNC reply, matching the exact key
/// names (PROJECT_OBSERVED from ciribob/DCS-SimpleRadioStandalone's
/// Common/Settings/Setting/ServerSettingsKeys.cs's DefaultServerSettings, read 2026-08-22) a real
/// SRS client expects to find.
///
/// DISTANCE_ENABLED is deliberately true -- NOT a leftover, this was a real bug found by reading
/// the client's own TCPClientHandler.cs (ClientRadioUpdatedAsync/ClientCoalitionUpdateAsync):
/// when both DISTANCE_ENABLED and LOS_ENABLED are false, the real client stops reporting its own
/// position at all (sends a zeroed LatLngPosition instead of its actual one), which is very
/// plausibly what put clients in the "lobby"/not-really-in-game UI state and suppressed actual
/// audio processing (see docs/SRS_BRIDGE.md's troubleshooting notes). LOS_ENABLED stays false --
/// there's no server-side terrain data to back it, and it's a separate flag from DISTANCE_ENABLED.
/// RADIO_EFFECT_OVERRIDE stays false so the client doesn't second-guess the server-baked DSP this
/// bridge will apply in a later phase.
/// </summary>
public static class SrsServerSettings
{
    /// <summary>Builds the settings dictionary sent in a SYNC reply, overlaying the
    /// admin-configured External AWACS Mode state onto the static defaults. Real SRS clients
    /// don't send the blue/red passwords back to the server for comparison here (that only
    /// happens in the connect dialog's password field, checked server-side in
    /// SrsClientAdapter.HandleExternalAwacsPasswordAsync) -- this dictionary only needs to report
    /// whether EAM is enabled at all.</summary>
    public static Dictionary<string, string> Build(SrsBridgeConfig config)
    {
        var settings = new Dictionary<string, string>(Defaults)
        {
            ["EXTERNAL_AWACS_MODE"] = config.ExternalAwacsModeEnabled ? "true" : "false"
        };
        return settings;
    }

    public static readonly Dictionary<string, string> Defaults = new()
    {
        { "CLIENT_EXPORT_ENABLED", "false" },
        { "COALITION_AUDIO_SECURITY", "false" },
        { "DISTANCE_ENABLED", "true" },
        { "EXTERNAL_AWACS_MODE", "false" },
        { "EXTERNAL_AWACS_MODE_BLUE_PASSWORD", "" },
        { "EXTERNAL_AWACS_MODE_RED_PASSWORD", "" },
        { "IRL_RADIO_RX_INTERFERENCE", "false" },
        { "IRL_RADIO_STATIC", "false" },
        { "IRL_RADIO_TX", "false" },
        { "LOS_ENABLED", "false" },
        { "RADIO_EXPANSION", "false" },
        { "SERVER_PORT", "5002" },
        { "SPECTATORS_AUDIO_DISABLED", "false" },
        { "CLIENT_EXPORT_FILE_PATH", "clients-list.json" },
        { "CHECK_FOR_BETA_UPDATES", "false" },
        { "ALLOW_RADIO_ENCRYPTION", "true" },
        { "TEST_FREQUENCIES", "" },
        { "SHOW_TUNED_COUNT", "true" },
        { "GLOBAL_LOBBY_FREQUENCIES", "" },
        { "LOTATC_EXPORT_ENABLED", "false" },
        { "LOTATC_EXPORT_PORT", "10712" },
        { "LOTATC_EXPORT_IP", "127.0.0.1" },
        { "UPNP_ENABLED", "false" },
        { "SHOW_TRANSMITTER_NAME", "false" },
        { "RETRANSMISSION_NODE_LIMIT", "0" },
        { "STRICT_RADIO_ENCRYPTION", "false" },
        { "TRANSMISSION_LOG_ENABLED", "false" },
        { "TRANSMISSION_LOG_RETENTION", "2" },
        { "RADIO_EFFECT_OVERRIDE", "false" },
        { "SERVER_IP", "0.0.0.0" },
        { "SERVER_PRESETS_ENABLED", "false" },
        { "HTTP_SERVER_ENABLED", "false" },
        { "SERVER_EAM_RADIO_PRESET_ENABLED", "false" },
        { "ALLOW_INSTRUCTOR_MODE", "false" },
    };
}
