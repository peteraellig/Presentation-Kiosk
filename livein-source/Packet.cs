using System.Buffers;

namespace LiveIn;

/// <summary>
/// One message for the page, shared by all clients that watch the same source.
/// The buffer comes from the array pool and goes back when the last client has sent it (reference count),
/// so 50 pictures of 4 MB per second do not churn the large object heap.
/// </summary>
internal sealed class Packet
{
    public const int HeaderSize = 16;
    public const byte TypeVideo = 1;
    public const byte TypeAudio = 2;
    public const byte TypeH264 = 3;

    public byte[] Buffer { get; }
    public int Length { get; }
    private int _refs = 1;

    private Packet(int length)
    {
        Buffer = ArrayPool<byte>.Shared.Rent(length);
        Length = length;
    }

    public static Packet Rent(int length) => new(length);

    public ReadOnlyMemory<byte> Memory => new(Buffer, 0, Length);

    public Packet AddRef()
    {
        Interlocked.Increment(ref _refs);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0) ArrayPool<byte>.Shared.Return(Buffer);
    }
}
