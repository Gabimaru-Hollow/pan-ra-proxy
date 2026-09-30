using System.Net;
using System.Text.Json;
using PanRaProxy.Radius;

namespace PanRaProxy.Tests.Radius;

/// <summary>
/// Synthetic packets from tests/fixtures/generate_radius_fixtures.py, built independently of the Proxy's code.
/// </summary>
public sealed record RadiusFixture(
    string Name,
    string Source,
    string Request,
    string Expect,
    string? Response,
    uint? StatusType,
    string? UserName,
    string[]? FramedIpAddresses)
{
    public byte[] RequestBytes => Convert.FromHexString(this.Request);

    public byte[] ResponseBytes => Convert.FromHexString(this.Response!);

    public IPAddress SourceAddress => IPAddress.Parse(this.Source);

    public override string ToString() => this.Name;
}

public static class RadiusFixtures
{
    private sealed record FixtureFile(Dictionary<string, string> Clients, List<RadiusFixture> Fixtures);

    private static readonly Lazy<FixtureFile> File = new(() =>
        JsonSerializer.Deserialize<FixtureFile>(
            System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "radius-accounting.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);

    public static IReadOnlyDictionary<string, string> Clients => File.Value.Clients;

    public static RadiusFixture Get(string name) => File.Value.Fixtures.Single(f => f.Name == name);

    public static IEnumerable<object[]> Accepted =>
        File.Value.Fixtures.Where(f => f.Expect == "Accepted").Select(f => new object[] { f });

    public static IEnumerable<object[]> Discarded =>
        File.Value.Fixtures.Where(f => f.Expect != "Accepted").Select(f => new object[] { f });

    /// <summary>
    /// The fixture addresses and loopback (used by the UDP tests), sharing the fixtures' one secret.
    /// </summary>
    public static RadiusClientRegistry Registry() =>
        new([.. Clients.Keys.Select(IPAddress.Parse), IPAddress.Loopback], Clients.Values.Single());
}
