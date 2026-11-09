using System.Text;
using RtlTcp.Core.Protocol;

namespace RtlTcp.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void V1_command_roundtrip_is_big_endian()
    {
        var cmd = new Command(RtlCommand.SetFrequency, 100_000_000);
        Assert.Equal(new byte[] { 0x01, 0x05, 0xF5, 0xE1, 0x00 }, cmd.ToArray());
        Assert.Equal(cmd, Command.Read(cmd.ToArray()));
    }

    [Fact]
    public void V1_negative_ppm_is_readable_as_signed()
    {
        var cmd = new Command(RtlCommand.SetFreqCorrection, unchecked((uint)-12));
        Assert.Equal(-12, Command.Read(cmd.ToArray()).SignedParam);
    }

    [Fact]
    public void V1_header_layout()
    {
        var h = new byte[V1.HeaderSize];
        V1.WriteHeader(h, TunerType.R820T, 29);
        Assert.Equal("RTL0"u8.ToArray().Concat(new byte[] { 0, 0, 0, 5, 0, 0, 0, 29 }), h);
    }

    [Fact]
    public void Hello_is_exactly_F2_RTL2()
    {
        Assert.Equal(new byte[] { 0xF2, 0x52, 0x54, 0x4C, 0x32 }, V2.Hello.ToArray());
        Assert.True(V2.IsHello(V2.Hello.ToArray()));
        Assert.False(V2.IsHello(new Command(RtlCommand.SetFrequency, V2.HelloMagic).ToArray()));
        Assert.False(V2.IsHello(new byte[] { 0xF2, 0x52 }));
    }

    [Fact]
    public void V2_header_roundtrip()
    {
        var h = new byte[V2.HeaderSize];
        V2.WriteHeader(h, V2HeaderFlags.None);
        Assert.Equal(new byte[] { 0x52, 0x54, 0x4C, 0x32, 0, 1, 0, 0, 0, 0, 0, 0 }, h);
        Assert.Equal((V2.ProtocolVersion, V2HeaderFlags.None), V2.ReadHeader(h));
    }

    [Fact]
    public void Frame_header_rejects_oversized_frames()
    {
        var h = new byte[FrameHeader.Size];
        new FrameHeader(FrameType.Iq, 16384).Write(h);
        Assert.Equal(new byte[] { 1, 0, 0, 0x40, 0 }, h);
        Assert.Equal(new FrameHeader(FrameType.Iq, 16384), FrameHeader.Read(h));

        h[1] = 0x7F;
        Assert.Throws<ProtocolException>(() => FrameHeader.Read(h));
    }

    [Fact]
    public void Status_serializes_with_snake_case_type_and_numeric_tuner()
    {
        var json = Encoding.UTF8.GetString(V2Json.Serialize(new StatusMessage(
            DeviceStates.Ready, "Device ready", new TunerInfo(TunerType.R820T, "R820T", [0, 9, 14]))));
        Assert.Equal(
            """{"type":"status","state":"ready","message":"Device ready","tuner":{"type":5,"name":"R820T","gains":[0,9,14]}}""",
            json);
    }

    [Fact]
    public void Error_omits_null_client()
    {
        var json = Encoding.UTF8.GetString(V2Json.Serialize(
            new ErrorMessage(ErrorCodes.Kicked, "Disconnected by administrator", true)));
        Assert.DoesNotContain("client", json);
    }

    [Fact]
    public void Deserialize_accepts_type_not_first_and_ignores_unknown_fields()
    {
        var msg = V2Json.Deserialize("""{"id":7,"extra":1,"type":"ping"}"""u8);
        Assert.Equal(new PingMessage(7), msg);
    }

    [Theory]
    [InlineData("""{"type":"something_new","x":1}""")]
    [InlineData("""{"no_type":true}""")]
    [InlineData("""not json""")]
    public void Deserialize_returns_null_for_unknown_or_invalid(string json)
    {
        Assert.Null(V2Json.Deserialize(Encoding.UTF8.GetBytes(json)));
    }
}
