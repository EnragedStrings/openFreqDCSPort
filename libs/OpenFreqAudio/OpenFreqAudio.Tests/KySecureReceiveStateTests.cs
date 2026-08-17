namespace OpenFreqAudio.Tests
{
    /// <summary>
    /// Covers the TRANSEC/COMSEC classification and the KY-58 wrong-variable state machine
    /// (Idle -&gt; Syncing -&gt; Beep -&gt; NoiseBurst -&gt; Muted -&gt; reset on carrier drop).
    /// </summary>
    public class KySecureReceiveStateTests
    {
        [Fact]
        public void Classify_BothClear_IsPlainPass()
        {
            var (matched, wrongKey, passive) = KySecureReceiveState.Classify(
                slotEnc: false, slotEncKey: 0, slotCryptoCapable: true, streamEnc: false, streamEncKey: 0);
            Assert.False(matched);
            Assert.False(wrongKey);
            Assert.False(passive);
        }

        [Fact]
        public void Classify_MatchingKeys_IsMatchedCipher()
        {
            var (matched, wrongKey, passive) = KySecureReceiveState.Classify(
                slotEnc: true, slotEncKey: 3, slotCryptoCapable: true, streamEnc: true, streamEncKey: 3);
            Assert.True(matched);
            Assert.False(wrongKey);
            Assert.False(passive);
        }

        [Fact]
        public void Classify_MismatchedKeys_IsWrongKey()
        {
            var (matched, wrongKey, passive) = KySecureReceiveState.Classify(
                slotEnc: true, slotEncKey: 1, slotCryptoCapable: true, streamEnc: true, streamEncKey: 2);
            Assert.False(matched);
            Assert.True(wrongKey);
            Assert.False(passive);
        }

        [Fact]
        public void Classify_CipherToNonCryptoCapableReceiver_IsPassiveCiphertext()
        {
            var (matched, wrongKey, passive) = KySecureReceiveState.Classify(
                slotEnc: false, slotEncKey: 0, slotCryptoCapable: false, streamEnc: true, streamEncKey: 1);
            Assert.False(matched);
            Assert.False(wrongKey);
            Assert.True(passive);
        }

        [Fact]
        public void Classify_CipherToCryptoCapableButNotEncrypted_IsPassiveCiphertext_NotWrongKey()
        {
            // Crypto-capable but not switched to cipher mode never attempts synchronization, so
            // it behaves like an ordinary receiver (continuous ciphertext texture) rather than
            // going through the sync-fail beep/burst/mute sequence.
            var (matched, wrongKey, passive) = KySecureReceiveState.Classify(
                slotEnc: false, slotEncKey: 0, slotCryptoCapable: true, streamEnc: true, streamEncKey: 1);
            Assert.False(matched);
            Assert.False(wrongKey);
            Assert.True(passive);
        }

        [Fact]
        public void Classify_ClearTxEncryptedRx_IsPlainPass()
        {
            // An encrypted (KY-58-engaged) receiver can still hear clear traffic normally.
            var (matched, wrongKey, passive) = KySecureReceiveState.Classify(
                slotEnc: true, slotEncKey: 1, slotCryptoCapable: true, streamEnc: false, streamEncKey: 0);
            Assert.False(matched);
            Assert.False(wrongKey);
            Assert.False(passive);
        }

        [Fact]
        public void Update_NoCarrier_StaysIdle()
        {
            var state = new KySecureReceiveState(48000);
            var outcome = state.Update(carrierPresent: false, matchedCipher: false, wrongKey: false,
                passiveCiphertext: false, frames: 480);
            Assert.Equal(KySecureOutcome.Idle, outcome);
        }

        [Fact]
        public void Update_ClearVoice_PassesImmediately()
        {
            var state = new KySecureReceiveState(48000);
            var outcome = state.Update(carrierPresent: true, matchedCipher: false, wrongKey: false,
                passiveCiphertext: false, frames: 480);
            Assert.Equal(KySecureOutcome.Pass, outcome);
        }

        [Fact]
        public void Update_MatchedCipher_PassesImmediately()
        {
            var state = new KySecureReceiveState(48000);
            var outcome = state.Update(carrierPresent: true, matchedCipher: true, wrongKey: false,
                passiveCiphertext: false, frames: 480);
            Assert.Equal(KySecureOutcome.Pass, outcome);
        }

        [Fact]
        public void Update_PassiveCiphertext_PersistsWhileCarrierPresent()
        {
            var state = new KySecureReceiveState(48000);
            for (int i = 0; i < 5; i++)
            {
                var outcome = state.Update(carrierPresent: true, matchedCipher: false, wrongKey: false,
                    passiveCiphertext: true, frames: 480);
                Assert.Equal(KySecureOutcome.PassiveCiphertext, outcome);
            }
        }

        [Fact]
        public void Update_WrongKey_RunsBeepThenNoiseBurstThenMuted_AndResetsOnCarrierDrop()
        {
            const int sampleRate = 48000;
            var state = new KySecureReceiveState(sampleRate);

            // Idle -> Syncing: first buffer after RF/wrong-key detected renders nothing yet.
            var outcome = state.Update(true, false, true, false, frames: 480);
            Assert.Equal(KySecureOutcome.Idle, outcome);

            // Drive enough buffers to exhaust the ~150ms sync window.
            KySecureOutcome last = outcome;
            for (int i = 0; i < 50 && last != KySecureOutcome.Beep; i++)
                last = state.Update(true, false, true, false, frames: 480);
            Assert.Equal(KySecureOutcome.Beep, last);

            // Drive through the beep (~200ms) into the noise burst (~300ms).
            for (int i = 0; i < 50 && last != KySecureOutcome.NoiseBurst; i++)
                last = state.Update(true, false, true, false, frames: 480);
            Assert.Equal(KySecureOutcome.NoiseBurst, last);

            // Drive through the noise burst into Muted, which then holds.
            for (int i = 0; i < 50 && last != KySecureOutcome.Muted; i++)
                last = state.Update(true, false, true, false, frames: 480);
            Assert.Equal(KySecureOutcome.Muted, last);
            Assert.Equal(KySecureOutcome.Muted, state.Update(true, false, true, false, frames: 480));

            // Carrier drops (transmission ends) -> resets to Idle for the next PTT.
            Assert.Equal(KySecureOutcome.Idle, state.Update(false, false, false, false, frames: 480));

            // Next transmission with the same wrong key runs the sequence again from the top.
            Assert.Equal(KySecureOutcome.Idle, state.Update(true, false, true, false, frames: 480));
        }
    }
}
