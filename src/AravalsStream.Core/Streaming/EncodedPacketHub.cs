using System.Buffers;
using System.IO.Pipes;
using AravalsStream.Core.Services;

namespace AravalsStream.Core.Streaming;

/// <summary>
/// Interface for an independent destination publisher endpoint subscribing to the EncodedPacketHub.
/// </summary>
public interface IEncodedPacketSubscriber
{
    string DestinationId { get; }
    void OnHeader(ReadOnlyMemory<byte> flvHeader);
    void OnPacket(ReadOnlyMemory<byte> packetData, EncodedPacketKind kind);
    void OnEncoderFailed(string reason);
}

public enum EncodedPacketKind
{
    Metadata,
    VideoSequenceHeader,
    AudioSequenceHeader,
    VideoKeyframe,
    VideoInterframe,
    Audio,
    Other
}

/// <summary>
/// Ingests encoded FLV/RTMP tags from a single shared hardware encoder,
/// caches stream sequence headers (SPS/PPS, AudioSpecificConfig),
/// and fans out encoded packets to multiple independent RTMP destination publishers.
/// </summary>
public sealed class EncodedPacketHub : IDisposable
{
    private readonly object _subscribersLock = new();
    private readonly List<IEncodedPacketSubscriber> _subscribers = [];
    private readonly CancellationTokenSource _cts = new();

    private byte[]? _cachedHeader;
    private byte[]? _cachedMetadataTag;
    private byte[]? _cachedVideoSeqHeaderTag;
    private byte[]? _cachedAudioSeqHeaderTag;
    private bool _disposed;

    public int SubscriberCount
    {
        get
        {
            lock (_subscribersLock) return _subscribers.Count;
        }
    }

    public void Subscribe(IEncodedPacketSubscriber subscriber)
    {
        lock (_subscribersLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EncodedPacketHub));
            if (!_subscribers.Contains(subscriber))
            {
                _subscribers.Add(subscriber);

                // Send cached headers immediately so reconnecting publisher can initialize its stream
                if (_cachedHeader is not null)
                {
                    subscriber.OnHeader(_cachedHeader);
                }
                if (_cachedMetadataTag is not null)
                {
                    subscriber.OnPacket(_cachedMetadataTag, EncodedPacketKind.Metadata);
                }
                if (_cachedVideoSeqHeaderTag is not null)
                {
                    subscriber.OnPacket(_cachedVideoSeqHeaderTag, EncodedPacketKind.VideoSequenceHeader);
                }
                if (_cachedAudioSeqHeaderTag is not null)
                {
                    subscriber.OnPacket(_cachedAudioSeqHeaderTag, EncodedPacketKind.AudioSequenceHeader);
                }
            }
        }
    }

    public void Unsubscribe(IEncodedPacketSubscriber subscriber)
    {
        lock (_subscribersLock)
        {
            _subscribers.Remove(subscriber);
        }
    }

    /// <summary>
    /// Processes a stream of FLV tags from the shared encoder pipe.
    /// Runs asynchronously until the pipe is closed or cancellation is requested.
    /// </summary>
    public async Task ProcessEncoderStreamAsync(Stream stream, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        var token = linkedCts.Token;

        try
        {
            // 1. Read FLV Header (9 bytes) + PreviousTagSize0 (4 bytes) = 13 bytes
            var headerBuf = new byte[13];
            await ReadExactAsync(stream, headerBuf, 0, 13, token).ConfigureAwait(false);
            lock (_subscribersLock)
            {
                _cachedHeader = headerBuf;
                foreach (var sub in _subscribers.ToArray())
                {
                    try { sub.OnHeader(headerBuf); }
                    catch (Exception ex)
                    {
                        AppLog.Write("EncodedPacketHub", $"Error sending header to {sub.DestinationId}: {ex.Message}");
                    }
                }
            }

            // 2. Loop reading FLV tags
            var tagHeaderBuf = new byte[11];
            while (!token.IsCancellationRequested)
            {
                // Read 11-byte FLV Tag Header
                var read = await stream.ReadAsync(tagHeaderBuf.AsMemory(0, 11), token).ConfigureAwait(false);
                if (read == 0) break; // EOF from encoder
                if (read < 11)
                {
                    await ReadExactAsync(stream, tagHeaderBuf, read, 11 - read, token).ConfigureAwait(false);
                }

                byte tagType = tagHeaderBuf[0];
                int dataSize = (tagHeaderBuf[1] << 16) | (tagHeaderBuf[2] << 8) | tagHeaderBuf[3];
                int totalPacketSize = 11 + dataSize + 4; // Header + Data + PreviousTagSize (4 bytes)

                var packetBuf = ArrayPool<byte>.Shared.Rent(totalPacketSize);
                try
                {
                    Buffer.BlockCopy(tagHeaderBuf, 0, packetBuf, 0, 11);
                    await ReadExactAsync(stream, packetBuf, 11, dataSize + 4, token).ConfigureAwait(false);

                    var kind = EncodedPacketKind.Other;

                    // Inspect tag payload for sequence headers and keyframes
                    if (tagType == 18) // Script / onMetaData
                    {
                        kind = EncodedPacketKind.Metadata;
                    }
                    else if (tagType == 9 && dataSize >= 2) // Video Tag
                    {
                        byte frameAndCodec = packetBuf[11];
                        byte avcPacketType = packetBuf[12];
                        int frameType = (frameAndCodec >> 4) & 0x0F;

                        kind = avcPacketType == 0 ? EncodedPacketKind.VideoSequenceHeader :
                            frameType == 1 ? EncodedPacketKind.VideoKeyframe : EncodedPacketKind.VideoInterframe;
                    }
                    else if (tagType == 8 && dataSize >= 2) // Audio Tag
                    {
                        byte soundFormat = (byte)((packetBuf[11] >> 4) & 0x0F);
                        byte aacPacketType = packetBuf[12];
                        if (soundFormat == 10 && aacPacketType == 0) // AAC Sequence Header (AudioSpecificConfig)
                            kind = EncodedPacketKind.AudioSequenceHeader;
                        else
                            kind = EncodedPacketKind.Audio;
                    }

                    // One immutable managed copy is shared by all publisher queues. The pooled
                    // read buffer can be returned immediately after dispatch completes.
                    var immutablePacket = new byte[totalPacketSize];
                    Buffer.BlockCopy(packetBuf, 0, immutablePacket, 0, totalPacketSize);

                    // Fan out packet to subscribers under lock
                    lock (_subscribersLock)
                    {
                        if (kind == EncodedPacketKind.Metadata) _cachedMetadataTag = immutablePacket;
                        if (kind == EncodedPacketKind.VideoSequenceHeader) _cachedVideoSeqHeaderTag = immutablePacket;
                        if (kind == EncodedPacketKind.AudioSequenceHeader) _cachedAudioSeqHeaderTag = immutablePacket;
                        foreach (var sub in _subscribers.ToArray())
                        {
                            try
                            {
                                sub.OnPacket(immutablePacket, kind);
                            }
                            catch (Exception ex)
                            {
                                AppLog.Write("EncodedPacketHub", $"Error dispatching to {sub.DestinationId}: {ex.Message}");
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(packetBuf);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write("EncodedPacketHub", $"Shared encoder stream failed: {ex.Message}");
            NotifyEncoderFailed(ex.Message);
        }
    }

    public void NotifyEncoderFailed(string reason)
    {
        lock (_subscribersLock)
        {
            foreach (var sub in _subscribers.ToArray())
            {
                try { sub.OnEncoderFailed(reason); }
                catch { }
            }
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException($"Unexpected end of stream. Expected {count} bytes, read {totalRead}.");
            totalRead += read;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();

        lock (_subscribersLock)
        {
            _subscribers.Clear();
        }
    }
}
