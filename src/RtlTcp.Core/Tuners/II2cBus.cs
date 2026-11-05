namespace RtlTcp.Core.Tuners;

public interface II2cBus
{
    void Write(byte address, ReadOnlySpan<byte> data);

    void Read(byte address, Span<byte> data);

    /// <summary>Tries to bring the bus back after a failed transfer (e.g. re-enable the repeater).</summary>
    void Recover();
}
