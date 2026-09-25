using System.Buffers.Binary;
using System.Net;
using System.Text;
using PanRaProxy.Mappings;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy.Tests.Mappings;

/// <summary>
/// The three username forms seen in the live capture: bare (no domain), UPN with several suffixes,
/// and NT4. Shared accounts (one user on several IPs) are covered here too.
/// </summary>
public class CanonicalUsernameTests
{
    private static UserIdOptions Options() => new()
    {
        Domain = new DomainOptions
        {
            DefaultNt4Domain = "XDOMAIN",
            DefaultUpnSuffix = "xdomain.local",
            UpnSuffixes = new(StringComparer.OrdinalIgnoreCase)
            {
                ["xdomain.local"] = "XDOMAIN",
                ["example.com"] = "XDOMAIN",
            },
        },
    };

    private static MappingDecider Decider(Action<UserIdOptions>? configure = null, INameTranslator? translator = null)
    {
        UserIdOptions options = Options();
        configure?.Invoke(options);
        return new MappingDecider(Microsoft.Extensions.Options.Options.Create(options), translator ?? new NoNameTranslation());
    }

    private static AccountingRequest Request(string user, params string[] ips)
    {
        byte[] status = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(status, AcctStatusType.Start);

        List<RadiusAttribute> attributes =
        [
            new(RadiusAttributeType.AcctStatusType, status),
            new(RadiusAttributeType.UserName, Encoding.UTF8.GetBytes(user)),
            .. ips.Select(ip => new RadiusAttribute(RadiusAttributeType.FramedIpAddress, IPAddress.Parse(ip).GetAddressBytes())),
        ];

        return new AccountingRequest(IPAddress.Parse("192.168.4.127"), 1, attributes);
    }

    private static IReadOnlyList<MappingChange> Changes(MappingDecision decision) =>
        Assert.IsType<MappingDecision.Changes>(decision).Items;

    [Theory]
    [InlineData("fabio.manfre", @"XDOMAIN\fabio.manfre")]              // bare: default domain
    [InlineData("fabio.manfre@xdomain.local", @"XDOMAIN\fabio.manfre")] // known suffix
    [InlineData("fabio.manfre@example.com", @"XDOMAIN\fabio.manfre")]   // second suffix, same domain
    [InlineData(@"XDOMAIN\fmanfre", @"XDOMAIN\fmanfre")]                // already NT4
    [InlineData("fabio.manfre@unknown.test", "fabio.manfre@unknown.test")] // unknown suffix: unchanged
    public void Domain_rules_produce_the_canonical_username(string raw, string canonical)
    {
        Assert.Equal(canonical, Assert.Single(Changes(Decider().Decide(Request(raw, "10.99.10.73")))).Mapping.Username);
    }

    [Fact]
    public void Without_domain_rules_every_form_is_passed_through()
    {
        MappingDecider decider = Decider(o => o.Domain = new DomainOptions());

        Assert.Equal("fabio.manfre", Assert.Single(Changes(decider.Decide(Request("fabio.manfre", "10.99.10.73")))).Mapping.Username);
        Assert.Equal("fabio.manfre@xdomain.local", Assert.Single(Changes(decider.Decide(Request("fabio.manfre@xdomain.local", "10.99.10.73")))).Mapping.Username);
    }

    [Fact]
    public void Name_translation_wins_over_the_suffix_map_when_the_account_name_differs()
    {
        // The live capture suggests sAMAccountName isn't name.surname: only a directory lookup gets this right.
        FakeTranslator translator = new()
        {
            ["fabio.manfre@xdomain.local"] = @"XDOMAIN\fmanfre",
        };
        MappingDecider decider = Decider(o => o.NameTranslation = true, translator);

        Assert.Equal(@"XDOMAIN\fmanfre", Assert.Single(Changes(decider.Decide(Request("fabio.manfre@xdomain.local", "10.99.10.73")))).Mapping.Username);

        // A bare username is looked up through the default UPN suffix.
        Assert.Equal(@"XDOMAIN\fmanfre", Assert.Single(Changes(decider.Decide(Request("fabio.manfre", "10.99.10.73")))).Mapping.Username);
    }

    [Fact]
    public void Failed_translation_falls_back_to_the_suffix_map()
    {
        MappingDecider decider = Decider(o => o.NameTranslation = true, new FakeTranslator());

        Assert.Equal(@"XDOMAIN\fabio.manfre", Assert.Single(Changes(decider.Decide(Request("fabio.manfre@example.com", "10.99.10.73")))).Mapping.Username);
    }

    [Fact]
    public void Rewrite_rules_run_before_the_domain_rules()
    {
        MappingDecider decider = Decider(o => o.UsernameRewrites = [new UsernameRewriteOptions { Match = "^guest-(.+)$", Replace = "$1@example.com" }]);

        Assert.Equal(@"XDOMAIN\anna", Assert.Single(Changes(decider.Decide(Request("guest-anna", "10.99.10.73")))).Mapping.Username);
    }

    [Fact]
    public void Shared_account_keeps_one_mapping_per_ip()
    {
        // Same user on several devices: the IP is what tells the Mappings apart.
        IReadOnlyList<MappingChange> changes = Changes(Decider().Decide(Request("fabio.manfre", "10.99.10.73", "10.99.10.74", "10.99.10.75")));

        Assert.Equal(3, changes.Count);
        Assert.All(changes, c => Assert.Equal(@"XDOMAIN\fabio.manfre", c.Mapping.Username));
        Assert.Equal(["10.99.10.73", "10.99.10.74", "10.99.10.75"], changes.Select(c => c.Mapping.IpAddress.ToString()));
    }

    [Fact]
    public void Shared_account_mappings_do_not_collapse_in_a_batch()
    {
        MappingBatcher batcher = new(Microsoft.Extensions.Options.Options.Create(new UserIdOptions { BatchWindowMs = 0 }), TimeProvider.System);
        foreach (MappingChange change in Changes(Decider().Decide(Request("fabio.manfre", "10.99.10.73", "10.99.10.74"))))
        {
            batcher.Enqueue(change);
        }

        Batch batch = batcher.ReadBatchAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(2, batch.Changes.Count);
    }

    private sealed class FakeTranslator : Dictionary<string, string>, INameTranslator
    {
        public string? TryTranslateToNt4(string upn) => this.GetValueOrDefault(upn);
    }
}
