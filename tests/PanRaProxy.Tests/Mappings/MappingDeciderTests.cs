using System.Buffers.Binary;
using System.Net;
using System.Text;
using PanRaProxy.Mappings;
using PanRaProxy.Options;
using PanRaProxy.Radius;
using PanRaProxy.Tests.Radius;

namespace PanRaProxy.Tests.Mappings;

public class MappingDeciderTests
{
    private static readonly UsernameRewriteOptions UpnToNt4 = new() { Match = @"^([^@\\]+)@domain\.local$", Replace = @"DOMAIN\$1" };

    private static MappingDecider Decider(Action<UserIdOptions>? configure = null, INameTranslator? translator = null)
    {
        UserIdOptions options = new() { UsernameRewrites = [UpnToNt4] };
        configure?.Invoke(options);
        return new MappingDecider(Microsoft.Extensions.Options.Options.Create(options), translator ?? new NoNameTranslation());
    }

    private static AccountingRequest Request(uint? status, string? user, params string[] framedIps)
    {
        List<RadiusAttribute> attributes = [];

        if (status is { } s)
        {
            byte[] value = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(value, s);
            attributes.Add(new RadiusAttribute(RadiusAttributeType.AcctStatusType, value));
        }

        if (user is not null)
        {
            attributes.Add(new RadiusAttribute(RadiusAttributeType.UserName, Encoding.UTF8.GetBytes(user)));
        }

        attributes.AddRange(framedIps.Select(ip => new RadiusAttribute(RadiusAttributeType.FramedIpAddress, IPAddress.Parse(ip).GetAddressBytes())));

        return new AccountingRequest(IPAddress.Parse("10.0.0.10"), 1, attributes);
    }

    private static MappingChange Single(MappingDecision decision) =>
        Assert.Single(Assert.IsType<MappingDecision.Changes>(decision).Items);

    private static void AssertDropped(DropReason reason, MappingDecision decision) =>
        Assert.Equal(new MappingDecision.Dropped(reason), decision);

    [Theory]
    [InlineData(AcctStatusType.Start)]
    [InlineData(AcctStatusType.InterimUpdate)]
    public void Start_and_interim_update_become_a_login_with_the_timeout(uint status)
    {
        MappingChange change = Single(Decider().Decide(Request(status, @"CONTOSO\mrossi", "10.20.30.40")));

        MappingChange.Login login = Assert.IsType<MappingChange.Login>(change);
        Assert.Equal(new Mapping(@"CONTOSO\mrossi", IPAddress.Parse("10.20.30.40")), login.Mapping);
        Assert.Equal(TimeSpan.FromMinutes(15), login.Timeout);
    }

    [Fact]
    public void Stop_is_dropped_while_logout_on_stop_is_off()
    {
        AssertDropped(DropReason.LogoutOnStopDisabled, Decider().Decide(Request(AcctStatusType.Stop, @"CONTOSO\mrossi", "10.20.30.40")));
    }

    [Fact]
    public void Stop_becomes_a_logout_when_logout_on_stop_is_on()
    {
        MappingChange change = Single(Decider(o => o.LogoutOnStop = true).Decide(Request(AcctStatusType.Stop, @"CONTOSO\mrossi", "10.20.30.40")));

        Assert.Equal(new MappingChange.Logout(new Mapping(@"CONTOSO\mrossi", IPAddress.Parse("10.20.30.40"))), change);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("255.255.255.254")]
    public void Placeholder_ip_never_becomes_a_mapping(string placeholder)
    {
        AssertDropped(DropReason.NoUsableIpAddress, Decider().Decide(Request(AcctStatusType.Start, @"CONTOSO\mrossi", placeholder)));
    }

    [Fact]
    public void Placeholder_ip_next_to_a_real_ip_is_skipped()
    {
        MappingChange change = Single(Decider().Decide(Request(AcctStatusType.Start, @"CONTOSO\mrossi", "255.255.255.255", "10.20.30.40")));

        Assert.Equal(IPAddress.Parse("10.20.30.40"), change.Mapping.IpAddress);
    }

    [Fact]
    public void Request_without_framed_ip_is_dropped()
    {
        AssertDropped(DropReason.NoUsableIpAddress, Decider().Decide(Request(AcctStatusType.Start, @"CONTOSO\mrossi")));
    }

    [Theory]
    [InlineData("host/pc01.contoso.local")]
    [InlineData(@"CONTOSO\PC01$")]
    [InlineData("HOST/PC01.contoso.local")]
    public void Machine_account_is_filtered(string user)
    {
        AssertDropped(DropReason.Filtered, Decider().Decide(Request(AcctStatusType.Start, user, "10.20.30.40")));
    }

    [Fact]
    public void Filter_sees_the_raw_user_name_not_the_rewritten_one()
    {
        // A Rewrite that would turn a Machine Account into something the Filter doesn't match.
        MappingDecider decider = Decider(o => o.UsernameRewrites = [new UsernameRewriteOptions { Match = "^host/(.+)$", Replace = @"DOMAIN\$1" }]);

        AssertDropped(DropReason.Filtered, decider.Decide(Request(AcctStatusType.Start, "host/pc01", "10.20.30.40")));
    }

    [Fact]
    public void Empty_filter_lets_machine_accounts_through()
    {
        MappingChange change = Single(Decider(o => o.UsernameFilter = "").Decide(Request(AcctStatusType.Start, @"CONTOSO\PC01$", "10.20.30.40")));

        Assert.Equal(@"CONTOSO\PC01$", change.Mapping.Username);
    }

    [Theory]
    [InlineData("mrossi@domain.local", @"DOMAIN\mrossi")]
    [InlineData("MRossi@DOMAIN.LOCAL", @"DOMAIN\MRossi")]
    [InlineData(@"CONTOSO\mrossi", @"CONTOSO\mrossi")]
    [InlineData("mrossi@other.example", "mrossi@other.example")]
    public void Username_rewrite_produces_the_canonical_username(string raw, string canonical)
    {
        MappingChange change = Single(Decider().Decide(Request(AcctStatusType.Start, raw, "10.20.30.40")));

        Assert.Equal(canonical, change.Mapping.Username);
    }

    [Fact]
    public void Rewrites_apply_in_order()
    {
        MappingDecider decider = Decider(o => o.UsernameRewrites =
        [
            new UsernameRewriteOptions { Match = "^corp\\\\", Replace = "CONTOSO\\" },
            new UsernameRewriteOptions { Match = "^CONTOSO\\\\(.+)$", Replace = "CONTOSO\\$1-x" },
        ]);

        Assert.Equal(@"CONTOSO\mrossi-x", Single(decider.Decide(Request(AcctStatusType.Start, @"corp\mrossi", "10.20.30.40"))).Mapping.Username);
    }

    [Fact]
    public void Name_translation_runs_only_when_on_and_only_for_a_upn_left_after_rewrite()
    {
        FakeTranslator translator = new() { ["mario.rossi@other.example"] = @"OTHER\mrossi" };

        MappingDecider off = Decider(translator: translator);
        MappingDecider on = Decider(o => o.NameTranslation = true, translator);

        Assert.Equal("mario.rossi@other.example", Single(off.Decide(Request(AcctStatusType.Start, "mario.rossi@other.example", "10.20.30.40"))).Mapping.Username);
        Assert.Equal(@"OTHER\mrossi", Single(on.Decide(Request(AcctStatusType.Start, "mario.rossi@other.example", "10.20.30.40"))).Mapping.Username);
        Assert.Equal(@"DOMAIN\mrossi", Single(on.Decide(Request(AcctStatusType.Start, "mrossi@domain.local", "10.20.30.40"))).Mapping.Username);
        Assert.Equal(["mario.rossi@other.example"], translator.Calls);
    }

    [Fact]
    public void Untranslatable_upn_keeps_its_rewritten_form()
    {
        MappingDecider decider = Decider(o => o.NameTranslation = true, new FakeTranslator());

        Assert.Equal("nobody@other.example", Single(decider.Decide(Request(AcctStatusType.Start, "nobody@other.example", "10.20.30.40"))).Mapping.Username);
    }

    [Theory]
    [InlineData(7u)] // Accounting-On
    [InlineData(8u)] // Accounting-Off
    public void Other_status_types_are_dropped(uint status)
    {
        AssertDropped(DropReason.UnsupportedStatusType, Decider().Decide(Request(status, @"CONTOSO\mrossi", "10.20.30.40")));
    }

    [Fact]
    public void Missing_user_name_or_status_type_is_dropped()
    {
        AssertDropped(DropReason.MissingAttributes, Decider().Decide(Request(null, @"CONTOSO\mrossi", "10.20.30.40")));
        AssertDropped(DropReason.MissingAttributes, Decider().Decide(Request(AcctStatusType.Start, null, "10.20.30.40")));
    }

    [Theory]
    [InlineData("start", "Login", @"CONTOSO\mrossi")]
    [InlineData("interim-update", "Login", @"CONTOSO\mrossi")]
    [InlineData("start-upn", "Login", @"DOMAIN\mrossi")]
    [InlineData("start-with-proxy-state", "Login", @"CONTOSO\mrossi")]
    [InlineData("stop", nameof(DropReason.LogoutOnStopDisabled), null)]
    [InlineData("start-machine-account", nameof(DropReason.Filtered), null)]
    [InlineData("start-placeholder-ip", nameof(DropReason.NoUsableIpAddress), null)]
    [InlineData("start-without-ip", nameof(DropReason.NoUsableIpAddress), null)]
    [InlineData("short-integer-attribute", nameof(DropReason.MissingAttributes), null)]
    public void Fixture_packets_end_to_end(string fixtureName, string expected, string? username)
    {
        RadiusFixture fixture = RadiusFixtures.Get(fixtureName);
        PacketVerdict.Accepted accepted = Assert.IsType<PacketVerdict.Accepted>(
            new AccountingPacketHandler(RadiusFixtures.Registry()).Handle(fixture.RequestBytes, fixture.SourceAddress));

        MappingDecision decision = Decider().Decide(accepted.Request);

        if (expected == "Login")
        {
            Assert.Equal(username, Assert.IsType<MappingChange.Login>(Single(decision)).Mapping.Username);
        }
        else
        {
            AssertDropped(Enum.Parse<DropReason>(expected), decision);
        }
    }

    private sealed class FakeTranslator : Dictionary<string, string>, INameTranslator
    {
        public List<string> Calls { get; } = [];

        public string? TryTranslateToNt4(string upn)
        {
            this.Calls.Add(upn);
            return this.GetValueOrDefault(upn);
        }
    }
}
