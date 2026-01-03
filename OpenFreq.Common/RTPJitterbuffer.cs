using OpenFreq.Common.Rtp;

namespace OpenFreq.Common;

/// <summary>
/// Adaptive RTP jitter buffer with timestamp-based playout scheduling.
/// Handles packet reordering, jitter smoothing, and adaptive buffer sizing.
/// </summary>
public class RtpJitterBuffer
{
    private class BufferedPacket
    {
        public RtpPacket Packet { get; set; }
        public DateTime ReceivedTime { get; set; }
        public uint PlayoutTimestamp { get; set; }
    }
        
    private readonly SortedDictionary<ushort, BufferedPacket> _buffer = new();
    private readonly Queue<double> _jitterSamples = new(50);
    private readonly int _sampleRate;
    private readonly int _maxBufferPackets;
        
    // State
    private ushort _nextExpectedSequence = 0;
    private uint _baseTimestamp = 0;
    private DateTime _baseTime = DateTime.MinValue;
    private DateTime _playoutStartTime = DateTime.MinValue;
    private DateTime _lastPacketReceived = DateTime.MinValue;
    private double _lastPacketTimestamp = 0;
    private bool _initialized = false;
        
    // Adaptive jitter buffer parameters
    private double _targetBufferMs = 60; // Start with 60ms
    private double _measuredJitterMs = 0;
    private const double MIN_BUFFER_MS = 30;
    private const double MAX_BUFFER_MS = 500;
        
    // Statistics
    private int _packetsReceived = 0;
    private int _packetsLost = 0;
    private int _packetsLate = 0;
    private int _packetsDuplicate = 0;
    private int _packetsPlayed = 0;
        
    public RtpJitterBuffer(int sampleRate = 48000, int maxBufferPackets = 50)
    {
        _sampleRate = sampleRate;
        _maxBufferPackets = maxBufferPackets;
    }
        
    /// <summary>
    /// Add packet to jitter buffer
    /// </summary>
    public void AddPacket(RtpPacket packet)
    {
        _packetsReceived++;
    
        // Initialize on first packet
        if (!_initialized)
        {
            _baseTimestamp = packet.Timestamp;
            _baseTime = DateTime.UtcNow;
            _playoutStartTime = _baseTime.AddMilliseconds(_targetBufferMs);  // Wait before playing
            _initialized = true;
    
            Console.WriteLine($"[JitterBuffer] Initialized: buffer={_targetBufferMs}ms, will start playout at {_playoutStartTime:HH:mm:ss.fff}");
        }

        lock (_buffer)
        {


            // Check for duplicate
            if (_buffer.ContainsKey(packet.SequenceNumber))
            {
                _packetsDuplicate++;
                return;
            }

            // Calculate playout time
            long timestampDiff = RtpPacket.TimestampDifference(packet.Timestamp, _baseTimestamp);
            double timestampMs = (timestampDiff * 1000.0) / _sampleRate;

            var bufferedPacket = new BufferedPacket
            {
                Packet = packet,
                ReceivedTime = DateTime.UtcNow,
                PlayoutTimestamp = packet.Timestamp
            };

            // Add to buffer
            _buffer[packet.SequenceNumber] = bufferedPacket;

            MeasureJitter(bufferedPacket, timestampMs);

            // Adapt buffer size
            AdaptBufferSize();

            // Limit buffer size
            while (_buffer.Count > _maxBufferPackets)
            {
                var oldest = _buffer.Keys.First();
                _buffer.Remove(oldest);
                Console.WriteLine($"[JitterBuffer] Buffer overflow, dropped seq {oldest}");
            }
        }
    }
        
    /// <summary>
    /// Get next packet ready for playout
    /// </summary>
    public RtpPacket? GetNextPacket()
    {
        if (!_initialized || _buffer.Count == 0)
            return null;
        
        var now = DateTime.UtcNow;
    
        if (now < _playoutStartTime)
        {
            return null;
        }
    
        // Calculate elapsed time since playout started
        var elapsedMs = (now - _playoutStartTime).TotalMilliseconds;
    
        // Calculate which timestamp we should be playing now
        uint playoutTimestamp = _baseTimestamp + (uint)((elapsedMs / 1000.0) * _sampleRate);

        KeyValuePair<ushort, BufferedPacket> nextPacket;
        lock (_buffer)
        {


            foreach (var kvp in _buffer.OrderBy(k => k.Key).Take(3))
            {
                var packetTS = kvp.Value.PlayoutTimestamp;
                var diff = RtpPacket.TimestampDifference(playoutTimestamp, packetTS);
            }

            // Find packets ready for playout
            var readyPackets = _buffer
                .Where(kvp => RtpPacket.TimestampDifference(playoutTimestamp, kvp.Value.PlayoutTimestamp) >= 0)
                .OrderBy(kvp => kvp.Key)
                .ToList();

            if (readyPackets.Count == 0)
            {
                return null;
            }

            // Get the packet with the lowest sequence number
            nextPacket = readyPackets.First();
            _buffer.Remove(nextPacket.Key);

            // Check if this is the expected sequence (loss detection)
            if (nextPacket.Key != _nextExpectedSequence)
            {
                int gap = RtpPacket.SequenceDifference(nextPacket.Key, _nextExpectedSequence);
                if (gap > 0)
                {
                    // Skipped packets (loss)
                    _packetsLost += gap;
                    Console.WriteLine(
                        $"[JitterBuffer] Packet loss: {gap} packets (seq {_nextExpectedSequence} to {nextPacket.Key - 1})");
                }
                else
                {
                    // Late packet
                    _packetsLate++;
                    Console.WriteLine(
                        $"[JitterBuffer] Late packet seq {nextPacket.Key} (expected {_nextExpectedSequence})");
                }
            }

            _nextExpectedSequence = (ushort)(nextPacket.Key + 1);
            _packetsPlayed++;
        }

        return nextPacket.Value.Packet;
    }
        
    /// <summary>
    /// Measure packet arrival jitter
    /// </summary>
    private void MeasureJitter(BufferedPacket packet, double expectedTimestampMs)
    {
        if (_lastPacketReceived == DateTime.MinValue)
        {
            // First packet - just record baseline
            _lastPacketReceived = packet.ReceivedTime;
            _lastPacketTimestamp = expectedTimestampMs;
            return;
        }
    
        double actualIntervalMs = (packet.ReceivedTime - _lastPacketReceived).TotalMilliseconds;
        double expectedIntervalMs = expectedTimestampMs - _lastPacketTimestamp;
    
        // Ignore abnormal timestamp deltas (first packet often has accumulated frames)
        if (expectedIntervalMs > 150)
        {
            Console.WriteLine($"[JitterBuffer] Ignoring abnormal timestamp delta: {expectedIntervalMs:F0}ms");
            _lastPacketReceived = packet.ReceivedTime;
            _lastPacketTimestamp = expectedTimestampMs;
            return;
        }
    
        // Detect transmission gap (PTT released)
        if (actualIntervalMs > 500 || expectedIntervalMs > 500)
        {
            Console.WriteLine($"[JitterBuffer] Transmission gap detected ({actualIntervalMs:F0}ms), resetting jitter measurement");
            _jitterSamples.Clear();
            _measuredJitterMs = 0;
            _lastPacketReceived = packet.ReceivedTime;
            _lastPacketTimestamp = expectedTimestampMs;
            return;
        }
    
        // Normal jitter calculation
        double jitter = Math.Abs(actualIntervalMs - expectedIntervalMs);
    
        _jitterSamples.Enqueue(jitter);
        if (_jitterSamples.Count > 50)
            _jitterSamples.Dequeue();
    
        if (_jitterSamples.Count >= 10)
        {
            _measuredJitterMs = _jitterSamples.Average();
        }
    
        _lastPacketReceived = packet.ReceivedTime;
        _lastPacketTimestamp = expectedTimestampMs;
    }
        
    /// <summary>
    /// Adapt buffer size based on observed jitter
    /// </summary>
    private void AdaptBufferSize()
    {
        if (_jitterSamples.Count < 10)
            return;
    
        // Use 95th percentile instead of max (handles occasional spikes)
        var sortedJitter = _jitterSamples.OrderBy(x => x).ToList();
        int p95Index = (int)(sortedJitter.Count * 0.95);
        double jitter95 = sortedJitter[p95Index];
    
        // Target buffer = 4x p95 jitter, minimum 60ms
        double targetBuffer = Math.Max(60, jitter95 * 4.0);
    
        // Adapt gradually (5% per adjustment = ~20 steps to converge)
        double delta = targetBuffer - _targetBufferMs;
        _targetBufferMs += delta * 0.05;
    }
    
        
    /// <summary>
    /// Get buffer statistics
    /// </summary>
    public (int received, int lost, int late, int duplicate, int played, 
        double lossPercent, double jitterMs, double bufferMs, int buffered) GetStatistics()
    {
        double lossPercent = (_packetsReceived + _packetsLost) > 0
            ? (100.0 * _packetsLost) / (_packetsReceived + _packetsLost)
            : 0.0;

        int bufferedCount;
        lock (_buffer)
        {
            bufferedCount = _buffer.Count;
        }

        return (
            _packetsReceived, 
            _packetsLost, 
            _packetsLate, 
            _packetsDuplicate, 
            _packetsPlayed,
            lossPercent, 
            _measuredJitterMs, 
            _targetBufferMs,
            bufferedCount
        );
    }
        
    /// <summary>
    /// Reset jitter buffer
    /// </summary>
    public void Reset()
    {
        lock (_buffer)
        {
            _buffer.Clear();
            _jitterSamples.Clear();
            _initialized = false;
            _baseTime = DateTime.MinValue;
            _playoutStartTime = DateTime.MinValue;
            _lastPacketReceived = DateTime.MinValue;
            _lastPacketTimestamp = 0;
            _packetsReceived = 0;
            _packetsLost = 0;
            _packetsLate = 0;
            _packetsDuplicate = 0;
            _packetsPlayed = 0;
        }
    }
        
    /// <summary>
    /// Get current buffer size in milliseconds
    /// </summary>
    public double GetBufferSizeMs() => _targetBufferMs;
        
    /// <summary>
    /// Set target buffer size (for manual override)
    /// </summary>
    public void SetTargetBufferSize(double milliseconds)
    {
        _targetBufferMs = Math.Clamp(milliseconds, MIN_BUFFER_MS, MAX_BUFFER_MS);
        Console.WriteLine($"[JitterBuffer] Manual buffer size: {_targetBufferMs:F0}ms");
    }
}