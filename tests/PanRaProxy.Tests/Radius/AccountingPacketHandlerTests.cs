using System.Net;
using PanRaProxy.Radius;

namespace PanRaProxy.Tests.Radius;

public class AccountingPacketHandlerTests
{
    private readonly AccountingPacketHandler handler = new(RadiusFixtures.Registry());

    [Theory]
    [MemberData(nameof(RadiusFixtures.Accepted), MemberType = typeof(RadiusFixtures))]
    public void Authentic_packet_is_accepted_with_the_rfc_response(RadiusFixture fixture)
    {
        PacketVerdict verdict = this.handler.Handle(fixture.RequestBytes, fixture.SourceAddress);

        PacketVerdict.Accepted accepted = Assert.IsType<PacketVerdict.Accepted>(verdict);
        Assert.Equal(fixture.Response, Convert.ToHexString(accepted.Response), ignoreCase: true);
        Assert.Equal(fixture.StatusType, accepted.Request.StatusType);
        Assert.Equal(fixture.UserName, accepted.Request.UserName);
        Assert.Equal(fixture.FramedIpAddresses, accepted.Request.FramedIpAddresses.Select(a => a.ToString()));
        Assert.Equal(fixture.SourceAddress, accepted.Request.Source);
    }

    [Theory]
    [MemberData(nameof(RadiusFixtures.Discarded), MemberType = typeof(RadiusFixtures))]
    public void Invalid_packet_is_discarded_with_its_reason(RadiusFixture fixture)
    {
        PacketVerdict verdict = this.handler.Handle(fixture.RequestBytes, fixture.SourceAddress);

        PacketVerdict.Discarded discarded = Assert.IsType<PacketVerdict.Discarded>(verdict);
        Assert.Equal(Enum.Parse<DiscardReason>(fixture.Expect), discarded.Reason);
    }

    [Fact]
    public void Tampered_attribute_fails_the_authenticator()
    {
        byte[] packet = RadiusFixtures.Get("start").RequestBytes;
        packet[^1] ^= 0x01; // last octet of Framed-IP-Address

        PacketVerdict verdict = this.handler.Handle(packet, IPAddress.Parse("10.0.0.10"));

        Assert.Equal(new PacketVerdict.Discarded(DiscardReason.BadAuthenticator), verdict);
    }

    [Fact]
    public void Ipv4_mapped_source_matches_its_ipv4_client()
    {
        RadiusFixture fixture = RadiusFixtures.Get("start");

        PacketVerdict verdict = this.handler.Handle(fixture.RequestBytes, fixture.SourceAddress.MapToIPv6());

        Assert.IsType<PacketVerdict.Accepted>(verdict);
    }

    [Fact]
    public void Arbitrary_bytes_never_throw()
    {
        Random random = new(2866);
        IPAddress source = IPAddress.Parse("10.0.0.10");
        byte[] valid = RadiusFixtures.Get("start-with-proxy-state").RequestBytes;

        for (int i = 0; i < 20_000; i++)
        {
            byte[] datagram = i % 2 == 0 ? new byte[random.Next(0, 120)] : (byte[])valid.Clone();

            if (i % 2 == 0)
            {
                random.NextBytes(datagram);
                if (datagram.Length > 0)
                {
                    datagram[0] = 4;
                }
            }
            else
            {
                datagram[random.Next(datagram.Length)] = (byte)random.Next(256);
            }

            _ = this.handler.Handle(datagram, source);
        }
    }
}
