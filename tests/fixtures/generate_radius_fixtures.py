"""Generate synthetic RADIUS accounting fixtures for PanRaProxy.Tests.

Written directly from RFC 2865 / RFC 2866 with hashlib, independently of the C#
implementation, so a bug in the Proxy's authenticator code can't pass its own tests.

Run from the repo root:  python tests/fixtures/generate_radius_fixtures.py
Output:                  tests/fixtures/radius-accounting.json

Secrets here are lab-only. Never put a production shared secret in this file.
"""

import hashlib
import ipaddress
import json
import pathlib
import struct

ACCOUNTING_REQUEST = 4
ACCOUNTING_RESPONSE = 5
ACCESS_REQUEST = 1

USER_NAME = 1
NAS_IP_ADDRESS = 4
FRAMED_IP_ADDRESS = 8
VENDOR_SPECIFIC = 26
CALLING_STATION_ID = 31
PROXY_STATE = 33
ACCT_STATUS_TYPE = 40
ACCT_SESSION_ID = 44

VENDOR_WING = 388  # Symbol / Extreme WiNG

CLIENT = "10.0.0.10"
SECRET = "lab-secret-1"
UNKNOWN_CLIENT = "10.0.0.99"


def attr(attr_type, value: bytes) -> bytes:
    return bytes([attr_type, len(value) + 2]) + value


def text(attr_type, value: str) -> bytes:
    return attr(attr_type, value.encode("utf-8"))


def integer(attr_type, value: int) -> bytes:
    return attr(attr_type, struct.pack("!I", value))


def ip(attr_type, value: str) -> bytes:
    return attr(attr_type, ipaddress.IPv4Address(value).packed)


def vsa(vendor_id: int, vendor_type: int, value: bytes) -> bytes:
    return attr(VENDOR_SPECIFIC, struct.pack("!I", vendor_id) + bytes([vendor_type, len(value) + 2]) + value)


def md5(data: bytes) -> bytes:
    return hashlib.md5(data).digest()


def accounting_request(identifier: int, attributes: bytes, secret: str, code: int = ACCOUNTING_REQUEST) -> bytes:
    """RFC 2866 §3: Request Authenticator = MD5(Code+Identifier+Length+16 zero octets+Attributes+Secret)."""
    length = 20 + len(attributes)
    header = struct.pack("!BBH", code, identifier, length)
    authenticator = md5(header + bytes(16) + attributes + secret.encode("utf-8"))
    return header + authenticator + attributes


def accounting_response(request: bytes, secret: str) -> bytes:
    """RFC 2866 §3: Response Authenticator = MD5(Code+ID+Length+RequestAuth+Attributes+Secret).

    RFC 2865 §5.33: every Proxy-State in the request is copied unmodified, in order.
    """
    identifier = request[1]
    request_length = struct.unpack("!H", request[2:4])[0]
    request_authenticator = request[4:20]

    proxy_states = b""
    index = 20
    while index < request_length:
        attr_type, attr_length = request[index], request[index + 1]
        if attr_type == PROXY_STATE:
            proxy_states += request[index:index + attr_length]
        index += attr_length

    length = 20 + len(proxy_states)
    header = struct.pack("!BBH", ACCOUNTING_RESPONSE, identifier, length)
    authenticator = md5(header + request_authenticator + proxy_states + secret.encode("utf-8"))
    return header + authenticator + proxy_states


def session(status: int, user: str, framed_ip: str | None, extra: bytes = b"") -> bytes:
    attributes = (
        integer(ACCT_STATUS_TYPE, status)
        + text(USER_NAME, user)
        + ip(NAS_IP_ADDRESS, "10.21.253.230")
        + text(CALLING_STATION_ID, "AA-BB-CC-DD-EE-FF")
        + text(ACCT_SESSION_ID, "0A15FDE6-00000042")
        + vsa(VENDOR_WING, 2, b"WLAN-Corp")
    )
    if framed_ip is not None:
        attributes += ip(FRAMED_IP_ADDRESS, framed_ip)
    return attributes + extra


def accepted(name, request, status, user, framed_ips, source=CLIENT, response=None):
    return {
        "name": name,
        "source": source,
        "request": request.hex(),
        "expect": "Accepted",
        "response": (response or accounting_response(request, SECRET)).hex(),
        "statusType": status,
        "userName": user,
        "framedIpAddresses": framed_ips,
    }


def discarded(name, request, reason, source=CLIENT):
    return {"name": name, "source": source, "request": request.hex(), "expect": reason}


def build():
    fixtures = []

    start = accounting_request(1, session(1, "CONTOSO\\mrossi", "10.20.30.40"), SECRET)
    fixtures.append(accepted("start", start, 1, "CONTOSO\\mrossi", ["10.20.30.40"]))

    interim = accounting_request(2, session(3, "CONTOSO\\mrossi", "10.20.30.40"), SECRET)
    fixtures.append(accepted("interim-update", interim, 3, "CONTOSO\\mrossi", ["10.20.30.40"]))

    stop = accounting_request(3, session(2, "CONTOSO\\mrossi", "10.20.30.40"), SECRET)
    fixtures.append(accepted("stop", stop, 2, "CONTOSO\\mrossi", ["10.20.30.40"]))

    upn = accounting_request(4, session(1, "mrossi@domain.local", "10.20.30.41"), SECRET)
    fixtures.append(accepted("start-upn", upn, 1, "mrossi@domain.local", ["10.20.30.41"]))

    machine = accounting_request(5, session(1, "host/pc01.contoso.local", "10.20.30.42"), SECRET)
    fixtures.append(accepted("start-machine-account", machine, 1, "host/pc01.contoso.local", ["10.20.30.42"]))

    placeholder = accounting_request(6, session(1, "CONTOSO\\mrossi", "255.255.255.255"), SECRET)
    fixtures.append(accepted("start-placeholder-ip", placeholder, 1, "CONTOSO\\mrossi", ["255.255.255.255"]))

    no_ip = accounting_request(7, session(1, "CONTOSO\\mrossi", None), SECRET)
    fixtures.append(accepted("start-without-ip", no_ip, 1, "CONTOSO\\mrossi", []))

    proxied = accounting_request(
        8,
        session(1, "CONTOSO\\mrossi", "10.20.30.40", attr(PROXY_STATE, b"nps-1") + attr(PROXY_STATE, b"\x00\x01\x02")),
        SECRET,
    )
    fixtures.append(accepted("start-with-proxy-state", proxied, 1, "CONTOSO\\mrossi", ["10.20.30.40"]))

    # Acct-Status-Type with a 3-octet value: structurally valid, semantically unusable.
    # Upstream's BitConverter threw on this and the unobserved exception stopped the listener.
    short_integer = accounting_request(9, attr(ACCT_STATUS_TYPE, b"\x00\x00\x01") + text(USER_NAME, "CONTOSO\\mrossi"), SECRET)
    fixtures.append(accepted("short-integer-attribute", short_integer, None, "CONTOSO\\mrossi", []))

    # RFC 2865 §3: octets beyond Length are padding and must be ignored.
    fixtures.append(accepted("trailing-padding", start + b"\x00" * 5, 1, "CONTOSO\\mrossi", ["10.20.30.40"],
                             response=accounting_response(start, SECRET)))

    wrong_secret = accounting_request(10, session(1, "CONTOSO\\mrossi", "10.20.30.40"), "not-the-secret")
    fixtures.append(discarded("wrong-secret", wrong_secret, "BadAuthenticator"))

    fixtures.append(discarded("unknown-client", start, "UnknownClient", source=UNKNOWN_CLIENT))

    access_request = accounting_request(11, text(USER_NAME, "CONTOSO\\mrossi"), SECRET, code=ACCESS_REQUEST)
    fixtures.append(discarded("access-request", access_request, "NotAccountingRequest"))

    fixtures.append(discarded("truncated", start[:-4], "Malformed"))
    fixtures.append(discarded("shorter-than-header", start[:19], "Malformed"))

    length_below_header = bytearray(start)
    struct.pack_into("!H", length_below_header, 2, 19)
    fixtures.append(discarded("length-below-header", bytes(length_below_header), "Malformed"))

    # Attribute whose length runs past the end of the packet, correctly signed.
    overrun = accounting_request(12, integer(ACCT_STATUS_TYPE, 1) + bytes([USER_NAME, 40]) + b"CONTOSO", SECRET)
    fixtures.append(discarded("attribute-overruns-packet", overrun, "Malformed"))

    # Attribute with length 1 (below the 2-octet minimum), correctly signed.
    too_small = accounting_request(13, integer(ACCT_STATUS_TYPE, 1) + bytes([USER_NAME, 1, 0]), SECRET)
    fixtures.append(discarded("attribute-length-below-minimum", too_small, "Malformed"))

    return {
        "comment": "Generated by generate_radius_fixtures.py. Do not edit by hand.",
        "clients": {CLIENT: SECRET},
        "fixtures": fixtures,
    }


if __name__ == "__main__":
    out = pathlib.Path(__file__).with_name("radius-accounting.json")
    out.write_text(json.dumps(build(), indent=2) + "\n", encoding="utf-8")
    print(f"wrote {out}")
