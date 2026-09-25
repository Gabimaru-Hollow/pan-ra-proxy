using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PanRaProxy.Mappings;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy.Tests.Mappings;

/// <summary>
/// The domain rules, against the three username forms seen in the live capture: bare (no domain),
/// UPN with several suffixes, and NT4. Shared Accounts are covered here too.
/// </summary>
public class CanonicalUsernameTests
{
    private const string BareUser = @"^(?<user>[^@\\]+)$";
    private const string AnyUpn = @"^(?<user>[^@\\]+)@(?<suffix>.+)$";

    /// <summary>The rules a site like the captured one would configure.</summary>
    private static List<DomainRuleOptions> SiteRules() =>
    [
        new() { Match = @"^(?<user>[^@\\]+)@(xdomain\.local|example\.com)$", Nt4Domain = "XDOMAIN" },
        new() { Match = BareUser, Nt4Domain = "XDOMAIN" },
    ];

    private static CanonicalUsernameResolver Resolver(List<DomainRuleOptions>? rules = null, INameTranslator? translator = null, Action<UserIdOptions>? configure = null)
    {
        UserIdOptions options = new() { Domain = new DomainOptions { Rules = rules ?? SiteRules() } };
        configure?.Invoke(options);
        return new CanonicalUsernameResolver(CanonicalUsernameRules.Create(options), translator ?? new NoNameTranslation());
    }

    /// <summary>The Mapping decision with the site's rules, for what only a whole request shows (Shared Accounts).</summary>
    private static MappingDecider Decider() =>
        new(Microsoft.Extensions.Options.Options.Create(new UserIdOptions()), Resolver());

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

    private static string Username(CanonicalUsernameResolver resolver, string raw) => resolver.Resolve(raw);

    [Theory]
    [InlineData("fabio.manfre", @"XDOMAIN\fabio.manfre")]                // bare
    [InlineData("fabio.manfre@xdomain.local", @"XDOMAIN\fabio.manfre")]  // first suffix
    [InlineData("fabio.manfre@example.com", @"XDOMAIN\fabio.manfre")]    // second suffix, same domain
    [InlineData(@"XDOMAIN\fabio.manfre", @"XDOMAIN\fabio.manfre")]       // already NT4: no rule matches
    [InlineData("guest@partner.test", "guest@partner.test")]             // unmapped suffix: unchanged
    public void First_matching_rule_decides(string raw, string canonical)
    {
        Assert.Equal(canonical, Username(Resolver(), raw));
    }

    [Fact]
    public void Rules_are_tried_in_order()
    {
        List<DomainRuleOptions> rules =
        [
            new() { Match = @"^(?<user>[^@\\]+)@contractors\.example$", Nt4Domain = "PARTNER" },
            new() { Match = AnyUpn, Nt4Domain = "XDOMAIN" },
        ];

        Assert.Equal(@"PARTNER\anna", Username(Resolver(rules), "anna@contractors.example"));
        Assert.Equal(@"XDOMAIN\bruno", Username(Resolver(rules), "bruno@xdomain.local"));
    }

    [Fact]
    public void Replace_builds_the_name_from_any_group()
    {
        List<DomainRuleOptions> rules =
        [
            new() { Match = @"^(?<first>[^.@\\]+)\.(?<last>[^@\\]+)@(?<suffix>.+)$", Replace = @"XDOMAIN\${last}.${first}" },
        ];

        Assert.Equal(@"XDOMAIN\manfre.fabio", Username(Resolver(rules), "fabio.manfre@xdomain.local"));
    }

    [Fact]
    public void Lookup_asks_the_directory_and_wins()
    {
        // For the accounts where the naming convention doesn't hold, only the directory is right.
        FakeTranslator translator = new() { ["fabio.manfre@xdomain.local"] = @"XDOMAIN\fmanfre" };
        List<DomainRuleOptions> rules =
        [
            new() { Match = BareUser, Lookup = "${user}@xdomain.local", Nt4Domain = "XDOMAIN" },
            new() { Match = AnyUpn, Lookup = "${user}@${suffix}", Nt4Domain = "XDOMAIN" },
        ];

        Assert.Equal(@"XDOMAIN\fmanfre", Username(Resolver(rules, translator), "fabio.manfre"));
        Assert.Equal(@"XDOMAIN\fmanfre", Username(Resolver(rules, translator), "fabio.manfre@xdomain.local"));
    }

    [Fact]
    public void Lookup_that_fails_falls_back_to_the_rule()
    {
        List<DomainRuleOptions> rules =
        [
            new() { Match = BareUser, Lookup = "${user}@xdomain.local", Nt4Domain = "XDOMAIN" },
            new() { Match = AnyUpn, Lookup = "${user}@${suffix}" },
        ];
        CanonicalUsernameResolver resolver = Resolver(rules, new FakeTranslator());

        Assert.Equal(@"XDOMAIN\unknown", Username(resolver, "unknown"));          // falls back to Nt4Domain
        Assert.Equal("unknown@partner.test", Username(resolver, "unknown@partner.test")); // no fallback: unchanged
    }

    [Fact]
    public void Lookups_are_cached_so_interim_updates_do_not_hit_the_directory_again()
    {
        FakeTranslator translator = new() { ["fabio.manfre@xdomain.local"] = @"XDOMAIN\fmanfre" };
        FakeTimeProvider time = new();
        CachingNameTranslator caching = new(translator, time, TimeSpan.FromMinutes(480));

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(@"XDOMAIN\fmanfre", caching.TryTranslateToNt4("fabio.manfre@xdomain.local"));
        }

        Assert.Single(translator.Calls);

        time.Advance(TimeSpan.FromMinutes(481));
        Assert.Equal(@"XDOMAIN\fmanfre", caching.TryTranslateToNt4("fabio.manfre@xdomain.local"));
        Assert.Equal(2, translator.Calls.Count);
    }

    [Fact]
    public void Failed_lookups_are_retried_sooner_than_successful_ones()
    {
        FakeTranslator translator = new();
        FakeTimeProvider time = new();
        CachingNameTranslator caching = new(translator, time, TimeSpan.FromMinutes(480));

        Assert.Null(caching.TryTranslateToNt4("unknown@xdomain.local"));
        Assert.Null(caching.TryTranslateToNt4("unknown@xdomain.local"));
        Assert.Single(translator.Calls);

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(caching.TryTranslateToNt4("unknown@xdomain.local"));
        Assert.Equal(2, translator.Calls.Count);
    }

    [Fact]
    public void Rewrite_rules_run_before_the_domain_rules()
    {
        CanonicalUsernameResolver resolver = Resolver(configure: o => o.UsernameRewrites = [new UsernameRewriteOptions { Match = "^guest-(.+)$", Replace = "$1@example.com" }]);

        Assert.Equal(@"XDOMAIN\anna", Username(resolver, "guest-anna"));
    }

    [Fact]
    public void Without_rules_every_form_is_passed_through()
    {
        CanonicalUsernameResolver resolver = Resolver([]);

        Assert.Equal("fabio.manfre", Username(resolver, "fabio.manfre"));
        Assert.Equal("fabio.manfre@xdomain.local", Username(resolver, "fabio.manfre@xdomain.local"));
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
    public async Task Shared_account_mappings_do_not_collapse_in_a_batch()
    {
        MappingBatcher batcher = new(Microsoft.Extensions.Options.Options.Create(new UserIdOptions { BatchWindowMs = 0 }), TimeProvider.System);
        foreach (MappingChange change in Changes(Decider().Decide(Request("fabio.manfre", "10.99.10.73", "10.99.10.74"))))
        {
            batcher.Enqueue(change);
        }

        Batch batch = await batcher.ReadBatchAsync(CancellationToken.None);

        Assert.Equal(2, batch.Changes.Count);
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
