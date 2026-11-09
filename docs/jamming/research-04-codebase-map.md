# 04 — OpenFreq codebase map for jamming (from Explore agent, 2026-11-09, main @ a8cd8b0)

No jamming code exists. No WOD/TOD/net model, no SINCGARS hopping state, no DCS Hooks/mission scripts.

## Receive DSP (libs/OpenFreqAudio/OpenFreqAudio/RadioPlayback.cs)
- _dspProc :1571. Snapshot & group streams by exact kHz :1599-1638.
- Per-stream drain :1653-1703: CarrierSmoother.Step smooths ReceivedSnrDb & TuneOffsetPPM; carrier amplitude = 10^(SNR/20) (noise unit power); PhaseStep :2242; SignalBlocked hard reset :1676.
- Per-slot loop :1707-2094: TRANSEC gate :1804 (HqOn equality only), dominant = transecStreams[0] :1805, KySecureReceiveState :1808-1859; per-tx arrays :1866-1893; SATCOM ProcessSatcomSlot :1895-1906 (body :1522-1563; FER/burst only, no I/Q); FM capture ApplyFmCaptureEffect :1487 (sigmoid 1 dB knee); fmNoiseScale from maxPower :1928-1951; I/Q sum :1956-1983; envelope :1986; AGC :1989; StepSquelch :1998 (hysteresis, SquelchCloseRatio 0.5 :56, :292-314); CVSD :2001-2005; noise-only branch :2014-2036; bandpass :2038-2043; NaN guard :2050-2061; pan/binaural :2076-2093; carrier phase advance :2099.
- INJECTION POINT: add jammer complex baseband to i/q at :1962-1963 and :2020-2021 (after noise, before envelope/AGC/squelch). Jammer amplitude = 10^(JNR/20). Free: AM beats, AGC capture/pumping, squelch break. Must also: include jammer in FM maxPower/capture weights; fmNoiseScale on SINR; SATCOM via FER (SetSatcomState :1245).
- RadioConfig :169-353 (per-slot state). Setter pattern: TuneFrequency :1188, SetSquelchLevel :1220, SetSatcomState :1245, SetSlotEncryption :1281; IPlaybackService.cs:35-57.
- BackgroundNoiseGenerator: unit-power complex noise, 8 kHz IF :93, thermal + Poisson log-normal impulses, ITU P.372 scaling :138-165; PrecipitationStatic :228 / _externalGain :320 unused "floor raise" knob.
- AudioParams (FastPathAudioSim.cs:63-104): ReceivedDb, ReceivedSnrDb, FreeSpaceLossDb, TerrainLossDb, RadioFrequencyKHz, TuneOffsetPPM, TerrainProfile, SignalBlocked, BlockedReason.

## Propagation reuse
- FastPathAudioSim.CalculateAudioParams(tx xyz, rx xyz, kHz, ppm, txPowerWatts, rxSensDbm, ...) :292-503 — FSPL, two-ray :383-474, Doppler :477-490, Delta-Bullington terrain :495-499; SNR = ReceivedDb − rxSens. Directly reusable for jammer→rx (fold antenna gain into ERP). Wrapped by ISignalCalculator/TerrainSignalCalculator (mockable).
- FreeSpacePathModel (lat/lon based; server-safe; used by SRS bridge SrsSignalQuality.cs:22).
- Frames: heightmap (x,y,alt m); DcsHeightmapConverter.ToHeightmap (x→x, z→y, y→alt − origin); GetOwnPosition :1577-1618; GetOwnDcsLocalPosition :1620; GetOwnGeodeticPosition :1638. No C# lat/lon→DCS-local projection (only Lua coord.LLtoLO :1442).
- DCS LOS: RequestDcsLineOfSight :2195 → DcsExportService.RequestLineOfSight(key, DcsVector3) :204 (250 ms throttle, 2 s TTL); arbitrary key ("jam:{id}") OK; ApplyDcsLineOfSightLoss :2210 (0..1 → 0-60 dB, blocked 180 dB); Lua calculateLosLoss :1347-1379; refresh pattern PrewarmDcsLineOfSightAsync :2306.
- Server LOS: LosOracleService.TryGetLineOfSight :66 via a live DCS client.

## HAVE QUICK / SINCGARS / SATCOM
- HQ: Lua getArc210ComsecState :423-439 → HqOn only for ARC-210 "CT-TD"; ARC-164 & others hqOn=false. → DcsRadioState.HqOn → ChannelCardViewModel.HqOn (+ manual checkbox ChannelCardView.axaml:423) → SetSlotEncryption. TX: FrequencyTransmission.HqOn. RX: RadioStream.HqOn → gate :1804. No WOD/net/TOD anywhere.
- Hop-fraction hook at :1804-1806: HQ slots get jammer gated by per-dwell pseudo-random mask with p = overlap/hopset width; non-HQ slots only when jammer covers slot freq. SINCGARS: same for FM 30-88 MHz.
- COMSEC stays below RF layer (KySecureReceiveState).
- SATCOM: SatcomLinkEngineCore.EvaluateLeg (Common/Satcom/SatcomLinkEngineCore.cs:20), add I0 at N0 :82-83 → Combine :93-118 → FER → SatcomLinkStateMessage (500 ms) → client SetSatcomState.

## Server
- OpenFreq.Server.json → ServerConfig (camelCase); catalog pattern SatcomServerConfig :78-170; register in Server/Json/ServerJsonContext.cs.
- World-entity broadcast precedent: SatelliteEphemerisUpdate every 10 s (SignalingServer.cs:850-856), DTO SatcomSatelliteInfoDto (SignalingMessages.cs:314-331), client OpenFreqRtcClient.cs:778.
- New message = SignalingMessageTypes const + SignalingMessages class + SignalingMessageFactory.Create* + OpenFreqJsonContext [JsonSerializable].
- DcsPresenceUpdate gives server each client's lat/lon/alt (ClientSession.cs:43-47).
- SRS bridge listeners would need server-side jamming degradation (SrsSignalQuality).

## DCS side
- Export environment only (Export.lua → OpenFreqDCS.lua, LuaExportAfterNextFrame). Packet buildPacket :1647-1714; UDP 34321; LOS on 34322.
- LoGetWorldObjects not used (could enumerate units by name prefix; MP servers may restrict export).
- Add-on radio provider mechanism :1538-1594.
- Options for mission jammers: (a) export LoGetWorldObjects "JAM_" prefix; (b) server GameGUI hook reading mission units → push to OpenFreq server (authoritative); (c) server config.

## Client
- Streams per peer "{peer}:{kHz}" CreateAudioStreamForPeer :1797; OnClientAudioDataReceived :1896; CalculateAudioParamsSync :1983.
- Do NOT model jammer as fake push stream (TRANSEC filter, dominant/COMSEC corruption, exact-freq only, gap logic). Use IPlaybackService.SetSlotInterference(freq, slot, contributions) fed by periodic timer computing J/N per slot (ISignalCalculator + RequestLineOfSight + GetRxSensitivity :2120).
- UI template: SignalBlockedText (ChannelCardViewModel :153-163; ChannelCardView.axaml:101-109).

## Testing
- OpenFreqAudio.Tests (seeded BackgroundNoiseGenerator), but DSP lambda bound to BASS (:2163) — extract per-slot mix into internal static function first.
- Client ServiceHarness; Server SignalingServerHarness/RtcClientHarness/LosOracleServiceTests/SatcomLinkEngineTests; Common SatcomLinkBudgetTests.

## Risks
No WOD/TOD in DCS; CPU in hot loop; SATCOM FER-only; no C# LL→LO; LoGetWorldObjects restriction; LOS throttle; must not trip SignalBlocked/gap logic; NaN guard must include jammer; jamming applied client-side (no server authority).
