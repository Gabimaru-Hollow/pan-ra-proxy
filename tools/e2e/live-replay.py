"""End-to-end replay of a live RADIUS accounting capture against the real Proxy.

Starts a mock PAN-OS User-ID endpoint over HTTPS, starts PanRaProxy pointed at it, replays the
Accounting-Requests from a pcap with their original timing, and prints every Login and Logout the
Proxy sends, live. At the end it compares the Mappings received with those expected from the
FreeRADIUS detail file of the same capture (a second, independent implementation of the rules).

Nothing is installed and no administrator rights are needed: the Proxy runs as a console process
with settings passed on the command line, and secrets come from environment variables.

Example:
    python tools/e2e/live-replay.py --pcap nps-acct-20260924-12.pcap --detail detail-20260924-13 \
        --secret testing123 --speed 30 --nt4-domain XDOMAIN --upn-suffix xdomain.local=XDOMAIN
"""
import argparse
import collections
import datetime
import http.server
import ipaddress
import os
import pathlib
import re
import socket
import ssl
import struct
import subprocess
import sys
import threading
import time
import urllib.parse
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[2]
PLACEHOLDER_IPS = {"0.0.0.0", "255.255.255.255", "255.255.255.254"}

start_time = time.monotonic()
received = []          # (kind, username, ip, timeout) in arrival order
batches = []           # entries per POST
lock = threading.Lock()


def stamp():
    return f"{time.monotonic() - start_time:7.2f}s"


def log(prefix, message):
    print(f"[{stamp()}] {prefix:<8} {message}", flush=True)


# --------------------------------------------------------------------------- mock PAN-OS endpoint

SUCCESS = ('<response status="success"><result><uid-response><version>2.0</version>'
           "<payload><login></login><logout></logout></payload></uid-response></result></response>")


def make_handler(reject):
    class Handler(http.server.BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def do_POST(self):
            body = self.rfile.read(int(self.headers.get("Content-Length", 0))).decode("utf-8")
            form = urllib.parse.parse_qs(body)
            key = self.headers.get("X-PAN-KEY")
            command = form.get("cmd", [""])[0]

            entries, rejected = [], []
            try:
                message = ET.fromstring(command)
            except ET.ParseError:
                log("FIREWALL", f"unparseable cmd: {command[:120]}")
                message = None

            if message is not None:
                for kind in ("login", "logout"):
                    for entry in message.findall(f"./payload/{kind}/entry"):
                        name, ip = entry.get("name", ""), entry.get("ip", "")
                        timeout = entry.get("timeout")
                        entries.append((kind, name, ip, timeout))
                        if reject and reject in name:
                            rejected.append((kind, name, ip))

            with lock:
                received.extend(entries)
                batches.append(entries)
                number = len(batches)

            logins = sum(1 for e in entries if e[0] == "login")
            logouts = len(entries) - logins
            log("BATCH", f"#{number}: {logins} login, {logouts} logout"
                        f"{'' if key else '  [!] no X-PAN-KEY header'}")
            for kind, name, ip, timeout in entries:
                marker = "  <-- rejected by mock" if any(r[1] == name and r[2] == ip for r in rejected) else ""
                log("", f"    {kind.upper():<6} {name:<32} {ip:<15} "
                        f"{'timeout=' + timeout + 'm' if timeout else '':<12}{marker}")

            payload = SUCCESS if not rejected else (
                '<response status="error"><msg><line><uid-response><version>2.0</version><payload>'
                + "".join(
                    f'<{kind}><entry name="{name}" ip="{ip}" message="Invalid user name"/></{kind}>'
                    for kind, name, ip in rejected)
                + "</payload></uid-response></line></msg></response>")

            data = payload.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/xml")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *args):
            pass

    return Handler


def self_signed_cert(directory):
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.x509.oid import NameOID

    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")])
    now = datetime.datetime.now(datetime.timezone.utc)
    certificate = (
        x509.CertificateBuilder()
        .subject_name(name).issuer_name(name).public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(minutes=5))
        .not_valid_after(now + datetime.timedelta(days=1))
        .add_extension(x509.SubjectAlternativeName([x509.DNSName("localhost")]), critical=False)
        .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
        .sign(key, hashes.SHA256())
    )

    cert_file = directory / "mock-panos.pem"
    key_file = directory / "mock-panos.key"
    cert_file.write_bytes(certificate.public_bytes(serialization.Encoding.PEM))
    key_file.write_bytes(key.private_bytes(serialization.Encoding.PEM,
                                           serialization.PrivateFormat.TraditionalOpenSSL,
                                           serialization.NoEncryption()))
    return cert_file, key_file


# --------------------------------------------------------------------------- capture parsing

def read_pcap(path):
    """Yields (timestamp, radius payload) for every Accounting-Request in the capture."""
    data = path.read_bytes()
    magic = data[:4]
    endian = "<" if magic == b"\xd4\xc3\xb2\xa1" else ">"
    if magic not in (b"\xd4\xc3\xb2\xa1", b"\xa1\xb2\xc3\xd4"):
        sys.exit(f"{path}: not a classic pcap file")

    offset = 24
    while offset + 16 <= len(data):
        sec, usec, caplen, _ = struct.unpack(endian + "IIII", data[offset:offset + 16])
        offset += 16
        packet, offset = data[offset:offset + caplen], offset + caplen
        if len(packet) < 14 or struct.unpack("!H", packet[12:14])[0] != 0x0800:
            continue
        ip = packet[14:]
        if ip[9] != 17:  # UDP
            continue
        udp = ip[(ip[0] & 0xF) * 4:]
        length = struct.unpack("!H", udp[4:6])[0]
        payload = udp[8:length]
        if len(payload) >= 20 and payload[0] == 4:  # Accounting-Request
            yield sec + usec / 1e6, payload


def read_detail(path):
    """The same packets, decoded by FreeRADIUS: used as the expected result."""
    records, current = [], None
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if line and not line.startswith("\t"):
            if current:
                records.append(current)
            current = {}
        elif current is not None and line.startswith("\t"):
            key, _, value = line.strip().partition(" = ")
            # FreeRADIUS escapes backslashes and quotes inside quoted values.
            current[key] = value.strip('"').replace("\\\\", "\\").replace('\\"', '"')
    if current:
        records.append(current)
    return records


def canonical(username, nt4_domain, upn_suffixes):
    if "\\" in username:
        return username
    if "@" in username:
        prefix, _, suffix = username.rpartition("@")
        domain = upn_suffixes.get(suffix.lower())
        return f"{domain}\\{prefix}" if domain else username
    return f"{nt4_domain}\\{username}" if nt4_domain else username


def expected_mappings(records, nt4_domain, upn_suffixes, username_filter, logout_on_stop):
    """Logins expected on the firewall, derived from the detail file by rules written here, not in C#."""
    mappings = set()
    for record in records:
        user = record.get("User-Name", "")
        ip = record.get("Framed-IP-Address", "")
        status = record.get("Acct-Status-Type", "")
        if not user or not ip or ip in PLACEHOLDER_IPS:
            continue
        if username_filter.search(user):
            continue
        if status in ("Start", "Interim-Update"):
            mappings.add((canonical(user, nt4_domain, upn_suffixes), ip))
        elif status == "Stop" and logout_on_stop:
            mappings.discard((canonical(user, nt4_domain, upn_suffixes), ip))
    return mappings


# --------------------------------------------------------------------------- the Proxy under test

def start_proxy(args, cert_file, https_port, log_file):
    settings = [
        f"--Radius:Port={args.radius_port}",
        "--Radius:Clients:0:Host=127.0.0.1",
        "--Radius:Clients:0:SecretName=RADIUS_SECRET",
        f"--Firewalls:Endpoints:0=https://localhost:{https_port}/api/",
        f"--Firewalls:CaFile={cert_file}",
        "--Firewalls:ApiKeySecretName=PAN_API_KEY",
        f"--UserId:TimeoutMinutes={args.timeout_minutes}",
        f"--UserId:InterimIntervalMinutes={args.interim_minutes}",
        f"--UserId:LogoutOnStop={'true' if args.logout_on_stop else 'false'}",
        f"--UserId:BatchWindowMs={args.batch_window_ms}",
    ]
    # Domain rules, in the same order the oracle applies them: known UPN suffixes first, bare names last.
    rules = [(rf"^(?<user>[^@\\]+)@{re.escape(suffix)}$", domain) for suffix, domain in args.upn_suffixes.items()]
    if args.nt4_domain:
        rules.append((r"^(?<user>[^@\\]+)$", args.nt4_domain))
    for index, (pattern, domain) in enumerate(rules):
        settings.append(f"--UserId:Domain:Rules:{index}:Match={pattern}")
        settings.append(f"--UserId:Domain:Rules:{index}:Nt4Domain={domain}")

    environment = dict(os.environ, RADIUS_SECRET=args.secret, PAN_API_KEY="e2e-dummy-key",
                       DOTNET_ENVIRONMENT="Development", Logging__LogLevel__PanRaProxy="Debug")

    handle = log_file.open("w", encoding="utf-8")
    process = subprocess.Popen([str(args.exe)] + settings, stdout=handle, stderr=subprocess.STDOUT,
                               cwd=str(args.exe.parent), env=environment, text=True)

    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if process.poll() is not None:
            print(log_file.read_text(encoding="utf-8", errors="replace")[-2000:])
            sys.exit("the Proxy exited during startup (see the configuration errors above)")
        if "Listening for RADIUS accounting" in log_file.read_text(encoding="utf-8", errors="replace"):
            return process, handle
        time.sleep(0.2)

    process.kill()
    sys.exit("the Proxy did not start within 30 s")


def watch_proxy_log(log_file, stop):
    """Mirrors the Proxy's warnings and errors into this console while the replay runs."""
    seen = 0
    while not stop.is_set():
        text = log_file.read_text(encoding="utf-8", errors="replace")
        lines = text.splitlines()
        for line in lines[seen:]:
            if line.startswith(("warn:", "fail:", "crit:")):
                log("PROXY", line.strip())
        seen = len(lines)
        time.sleep(0.3)


# --------------------------------------------------------------------------- replay

def replay(packets, args, sock, target):
    acked = identical = 0
    previous = None
    for index, (timestamp, payload) in enumerate(packets, start=1):
        if previous is not None:
            delay = (timestamp - previous) / args.speed
            if delay > 0:
                time.sleep(min(delay, args.max_gap))
        previous = timestamp

        sock.sendto(payload, target)
        try:
            response, _ = sock.recvfrom(4096)
            acked += 1
            identical += 1 if response[1] == payload[1] else 0
        except socket.timeout:
            log("REPLAY", f"packet {index}: no Accounting-Response")

        if index % 25 == 0:
            log("REPLAY", f"{index}/{len(packets)} packets sent")

    return acked, identical


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pcap", type=pathlib.Path, default=ROOT / "nps-acct-20260924-12.pcap")
    parser.add_argument("--detail", type=pathlib.Path, default=ROOT / "detail-20260924-13")
    parser.add_argument("--secret", default="testing123", help="RADIUS shared secret used in the capture")
    parser.add_argument("--speed", type=float, default=30.0, help="replay speed factor (1 = original timing)")
    parser.add_argument("--max-gap", type=float, default=5.0, help="cap on a single inter-packet pause, seconds")
    parser.add_argument("--radius-port", type=int, default=18131)
    parser.add_argument("--https-port", type=int, default=0, help="0 picks a free port")
    parser.add_argument("--timeout-minutes", type=int, default=15)
    parser.add_argument("--interim-minutes", type=int, default=5)
    parser.add_argument("--batch-window-ms", type=int, default=50)
    parser.add_argument("--logout-on-stop", action="store_true")
    parser.add_argument("--nt4-domain", default="", help="NT4 domain for bare usernames, e.g. XDOMAIN")
    parser.add_argument("--upn-suffix", action="append", default=[], metavar="SUFFIX=DOMAIN",
                        help="UPN suffix to NT4 domain, repeatable")
    parser.add_argument("--username-filter", default=r"(\$$|^host/)")
    parser.add_argument("--reject", default="", help="mock rejects entries whose username contains this text")
    parser.add_argument("--exe", type=pathlib.Path,
                        default=ROOT / "artifacts" / "publish" / "PanRaProxy.exe")
    args = parser.parse_args()
    args.upn_suffixes = dict(pair.split("=", 1) for pair in args.upn_suffix)
    args.upn_suffixes = {k.lower(): v for k, v in args.upn_suffixes.items()}

    for path in (args.pcap, args.detail, args.exe):
        if not path.exists():
            sys.exit(f"missing: {path}  (build the Proxy with build\\build-msi.ps1 or dotnet publish)")

    work = ROOT / "artifacts" / "e2e"
    work.mkdir(parents=True, exist_ok=True)
    cert_file, key_file = self_signed_cert(work)
    proxy_log = work / "proxy.log"

    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(cert_file, key_file)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", args.https_port), make_handler(args.reject))
    server.socket = context.wrap_socket(server.socket, server_side=True)
    https_port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()
    log("MOCK", f"PAN-OS User-ID endpoint on https://localhost:{https_port}/api/ (CA: {cert_file.name})")

    packets = list(read_pcap(args.pcap))
    records = read_detail(args.detail)
    span = packets[-1][0] - packets[0][0]
    log("CAPTURE", f"{len(packets)} Accounting-Requests over {span / 60:.1f} min, replayed at {args.speed}x")

    process, handle = start_proxy(args, cert_file, https_port, proxy_log)
    log("PROXY", f"running (pid {process.pid}), log: {proxy_log}")
    stop = threading.Event()
    threading.Thread(target=watch_proxy_log, args=(proxy_log, stop), daemon=True).start()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(2)
    sock.bind(("127.0.0.1", 0))

    try:
        acked, identical = replay(packets, args, sock, ("127.0.0.1", args.radius_port))
        time.sleep(2)  # let the last Batch go out
    finally:
        stop.set()
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
        handle.close()
        server.shutdown()
        sock.close()

    # ----------------------------------------------------------------- comparison
    with lock:
        logins = {(name, ip) for kind, name, ip, _ in received if kind == "login"}
        logouts = {(name, ip) for kind, name, ip, _ in received if kind == "logout"}
        timeouts = {t for kind, _, _, t in received if kind == "login"}
        entry_count = len(received)
        batch_count = len(batches)

    expected = expected_mappings(records, args.nt4_domain, args.upn_suffixes,
                                 re.compile(args.username_filter, re.IGNORECASE), args.logout_on_stop)

    print()
    log("RESULT", f"packets sent {len(packets)}, acknowledged {acked}, response id matched {identical}")
    log("RESULT", f"batches {batch_count}, entries {entry_count}: {len(logins)} distinct Logins, {len(logouts)} distinct Logouts")
    log("RESULT", f"login timeouts seen: {sorted(t for t in timeouts if t)} minutes")

    missing, unexpected = expected - logins, logins - expected
    if not missing and not unexpected:
        log("RESULT", f"OK: the {len(logins)} Mappings match the detail file exactly")
    else:
        log("RESULT", f"DIFFERENCES: {len(missing)} missing, {len(unexpected)} unexpected")
        for name, ip in sorted(missing)[:10]:
            log("", f"    missing    {name:<32} {ip}")
        for name, ip in sorted(unexpected)[:10]:
            log("", f"    unexpected {name:<32} {ip}")

    shapes = collections.Counter("NT4" if "\\" in n else "UPN" if "@" in n else "bare" for n, _ in logins)
    log("RESULT", f"canonical form of the Mappings: {dict(shapes)}")
    per_user = collections.Counter(name for name, _ in logins)
    shared = {n: c for n, c in per_user.items() if c > 1}
    log("RESULT", f"users holding several Mappings (shared accounts): {len(shared)}"
                  + (f", up to {max(shared.values())} IPs" if shared else ""))
    print(f"\nProxy log: {proxy_log}")
    return 0 if not missing and not unexpected else 1


if __name__ == "__main__":
    sys.exit(main())
