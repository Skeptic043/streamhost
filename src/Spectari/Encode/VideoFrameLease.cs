using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Spectari.Encode;

internal enum VideoPixelFormat
{
    Nv12,
}

internal enum FrameLeaseReturnReason
{
    InputReleased,
    InputRejected,
    Flush,
    Failure,
    Teardown,
}

internal readonly record struct GpuTextureFrame(
    nint NativeTexturePointer,
    int Width,
    int Height,
    VideoPixelFormat PixelFormat,
    string AdapterLuid);

internal readonly record struct FrameLeaseAccounting(
    int Capacity,
    int Available,
    int Outstanding,
    long TotalRents,
    IReadOnlyDictionary<FrameLeaseReturnReason, long> Returns);

/// <summary>
/// A non-blocking lease for one encode-sized NV12 texture. The encoder returns it
/// only after the input sample reference is released or the transform is shut down.
/// </summary>
internal sealed class VideoFrameLease
{
    private Nv12TexturePool? _owner;
    private readonly int _slotIndex;
    private readonly long _leaseId;

    internal VideoFrameLease(
        Nv12TexturePool owner,
        int slotIndex,
        long leaseId,
        GpuTextureFrame frame)
    {
        _owner = owner;
        _slotIndex = slotIndex;
        _leaseId = leaseId;
        Frame = frame;
    }

    internal GpuTextureFrame Frame { get; }
    internal ID3D11Texture2D? NativeTexture => _owner?.GetNativeTexture(_slotIndex, _leaseId);

    internal bool Return(FrameLeaseReturnReason reason)
    {
        Nv12TexturePool? owner = Interlocked.Exchange(ref _owner, null);
        return owner is not null && owner.Return(_slotIndex, _leaseId, reason);
    }
}

/// <summary>Fixed encode-surface pool with immediate failure on exhaustion.</summary>
internal sealed class Nv12TexturePool : IDisposable
{
    // A synthetic probe against this NVIDIA MFT measured a peak occupancy of 2
    // and a maximum input hold of one later submission. That probe fed blank
    // surfaces, not real 1440p content under load, so it sets the floor rather
    // than the number. Exhaustion is no longer fatal (the pull loop simply does
    // not submit that frame) but it is also no longer loud, so an under-sized
    // pool would cost unique frames silently. A surface is about 5 MB at 1440p,
    // which makes headroom far cheaper than the failure it prevents.
    internal const int DefaultCapacity = 6;

    private sealed class Slot
    {
        internal required GpuTextureFrame Frame { get; init; }
        internal ID3D11Texture2D? Texture { get; init; }
        internal long LeaseId { get; set; }
        internal bool InUse { get; set; }
    }

    private readonly object _gate = new();
    private readonly Slot[] _slots;
    private readonly Dictionary<FrameLeaseReturnReason, long> _returns =
        Enum.GetValues<FrameLeaseReturnReason>().ToDictionary(reason => reason, _ => 0L);
    private long _nextLeaseId;
    private long _totalRents;
    private bool _disposed;

    internal Nv12TexturePool(
        ID3D11Device device,
        int width,
        int height,
        string adapterLuid,
        int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ValidateCapacity(capacity);

        _slots = new Slot[capacity];
        try
        {
            for (int i = 0; i < capacity; i++)
            {
                var texture = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                });
                _slots[i] = new Slot
                {
                    Texture = texture,
                    Frame = new GpuTextureFrame(
                        texture.NativePointer,
                        width,
                        height,
                        VideoPixelFormat.Nv12,
                        adapterLuid),
                };
            }
        }
        catch
        {
            foreach (Slot? slot in _slots)
                slot?.Texture?.Dispose();
            throw;
        }
    }

    private Nv12TexturePool(int capacity)
    {
        ValidateCapacity(capacity);
        _slots = Enumerable.Range(0, capacity)
            .Select(index => new Slot
            {
                Frame = new GpuTextureFrame(
                    (nint)(index + 1),
                    2,
                    2,
                    VideoPixelFormat.Nv12,
                    "test-adapter"),
            })
            .ToArray();
    }

    internal static Nv12TexturePool CreateForTesting(int capacity) => new(capacity);

    internal bool TryRent(out VideoFrameLease? lease)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (int index = 0; index < _slots.Length; index++)
            {
                Slot slot = _slots[index];
                if (slot.InUse) continue;

                slot.InUse = true;
                slot.LeaseId = ++_nextLeaseId;
                _totalRents++;
                lease = new VideoFrameLease(this, index, slot.LeaseId, slot.Frame);
                return true;
            }
        }

        lease = null;
        return false;
    }

    internal FrameLeaseAccounting GetAccounting()
    {
        lock (_gate)
        {
            int outstanding = _slots.Count(slot => slot.InUse);
            return new FrameLeaseAccounting(
                _slots.Length,
                _slots.Length - outstanding,
                outstanding,
                _totalRents,
                new Dictionary<FrameLeaseReturnReason, long>(_returns));
        }
    }

    internal ID3D11Texture2D? GetNativeTexture(int slotIndex, long leaseId)
    {
        lock (_gate)
        {
            Slot slot = _slots[slotIndex];
            return slot.InUse && slot.LeaseId == leaseId ? slot.Texture : null;
        }
    }

    internal bool Return(int slotIndex, long leaseId, FrameLeaseReturnReason reason)
    {
        lock (_gate)
        {
            Slot slot = _slots[slotIndex];
            if (!slot.InUse || slot.LeaseId != leaseId)
                return false;

            slot.InUse = false;
            _returns[reason]++;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (Slot slot in _slots)
            {
                if (slot.InUse)
                {
                    slot.InUse = false;
                    _returns[FrameLeaseReturnReason.Teardown]++;
                }
                slot.Texture?.Dispose();
            }
            _disposed = true;
        }
    }

    private static void ValidateCapacity(int capacity)
    {
        if (capacity is < 2 or > DefaultCapacity)
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                $"NV12 pool capacity must be between two and {DefaultCapacity}.");
    }
}
