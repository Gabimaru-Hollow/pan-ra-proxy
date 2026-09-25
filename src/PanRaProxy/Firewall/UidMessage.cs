using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PanRaProxy.Mappings;

namespace PanRaProxy.Firewall;

/// <summary>
/// The PAN-OS User-ID XML: the <c>uid-message</c> sent for a Batch and the <c>response</c> parsed back.
/// </summary>
internal static class UidMessage
{
    /// <summary>
    /// PAN-OS reports this for a Logout of a Mapping it no longer holds (e.g. already timed out): not a failure.
    /// </summary>
    private const string DeleteMappingFailed = "delete mapping failed";

    public static string Serialize(Batch batch)
    {
        XElement message = new(
            "uid-message",
            new XElement("version", "1.0"),
            new XElement("type", "update"),
            new XElement(
                "payload",
                new XElement(
                    "login",
                    batch.Logins.Select(l => new XElement(
                        "entry",
                        new XAttribute("name", l.Mapping.Username),
                        new XAttribute("ip", l.Mapping.IpAddress),
                        new XAttribute("timeout", ((int)Math.Ceiling(l.Timeout.TotalMinutes)).ToString(CultureInfo.InvariantCulture))))),
                new XElement(
                    "logout",
                    batch.Logouts.Select(l => new XElement(
                        "entry",
                        new XAttribute("name", l.Mapping.Username),
                        new XAttribute("ip", l.Mapping.IpAddress))))));

        return message.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Null when the Firewall applied the Batch (possibly with <paramref name="rejections"/>);
    /// otherwise the reason the whole Batch was refused.
    /// </summary>
    public static string? ParseResponse(string body, out List<Rejection> rejections)
    {
        rejections = [];

        XElement response;
        try
        {
            response = XElement.Parse(body);
        }
        catch (XmlException)
        {
            return $"unparseable response: {Truncate(body)}";
        }

        string? status = response.Attribute("status")?.Value;

        if (response.Name != "response" || status is null)
        {
            return $"unexpected response: {Truncate(body)}";
        }

        if (status == "success")
        {
            return null;
        }

        XElement? payload = response.Element("msg")?.Element("line")?.Element("uid-response")?.Element("payload");

        List<Rejection> failedEntries = payload is null
            ? []
            : [.. Entries(payload, "login", RejectionKind.Login), .. Entries(payload, "logout", RejectionKind.Logout)];

        if (failedEntries.Count > 0)
        {
            // Per-entry failures: PAN-OS applied everything else in the Batch.
            rejections.AddRange(failedEntries.Where(r =>
                !(r.Kind == RejectionKind.Logout && r.Message.Equals(DeleteMappingFailed, StringComparison.OrdinalIgnoreCase))));
            return null;
        }

        string message = response.Element("result")?.Element("msg")?.Value
                         ?? response.Element("msg")?.Value
                         ?? Truncate(body);

        return string.IsNullOrWhiteSpace(message) ? $"status={status}" : message.Trim();
    }

    private static IEnumerable<Rejection> Entries(XElement payload, string name, RejectionKind kind) =>
        payload.Elements(name).Elements("entry").Select(e => new Rejection(
            kind,
            e.Attribute("name")?.Value ?? "",
            e.Attribute("ip")?.Value ?? "",
            e.Attribute("message")?.Value ?? ""));

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
