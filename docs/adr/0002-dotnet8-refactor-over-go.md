---
status: accepted
---

# Refactor to .NET 8 rather than rewrite in Go

For now the Proxy is refactored from .NET Framework 4.6.2 to .NET 8 as a Windows service, rather than rewritten in Go. The refactor keeps the upstream code that already works (RADIUS attribute parsing, Request/Response Authenticator, per-entry XML API response validation), the Event Log integration, and Name Translation, which needs a Windows host.

Revisit this if the Proxy has to run on Linux or in a container. Name Translation is off by default, so nothing in the domain model ties the Proxy to Windows.

## Support window

.NET 8 support ends on 10 November 2026. The first MSI ships on .NET 8 (decided 2026-09-21); retarget to .NET 10 (LTS, supported to November 2028) before that date.

## Amendment (step 2 of the build order)

The upstream attribute parser (`RadiusAttribute.cs`) was not carried over. It throws on unexpected value lengths (the cause of the silent listener death), mutates its input buffer, and most of its 521 lines describe attributes the Proxy never reads. It was replaced by a small attribute reader that never throws on packet content. The authenticator and Proxy-State behaviour is unchanged and is verified against fixtures generated independently from RFC 2866.

## Considered Options

- **Go rewrite** (`layeh.com/radius`, roughly 300–400 lines): no Windows dependency and runs in a container, but it rewrites the already-tested authenticator and response code, so the unit tests in the refactoring spec (§5.5) would be needed from day one. It also loses Name Translation and native Event Log integration.
