# Security and current limitations

**Experimental; not security reviewed and not ready for sensitive data or general release.** The app does not yet implement the originally required persistent per-install hardware/platform-secured identity, trusted-peer storage, SAS comparison, or revoked/changed identity warning. It generates ephemeral self-signed certificates for a session. A new invitation code is required for each connection. An optional session-only auto-accept policy is disabled by default and described below.

The invitation includes a receiver address, port, SHA-256 certificate fingerprint, and random 256-bit bearer secret. It must be shared privately through an authenticated out-of-band channel. Anyone who obtains it can attempt to use it; clipboard and communications used to share it can leak it. A short PIN without a reviewed PAKE protocol would be vulnerable to offline guessing, so this implementation uses a long random code. It is not a persistent trust relationship. The in-memory verifier expires after ten minutes, consumes a valid code once, and loses it on restart. An attacker can deny service by connecting first or consuming a leaked code; there is no attempt rate limit. No invitation code, secret or fingerprint is sent in discovery; a random session ID, display name, OS, protocol version and port are visible to the LAN. Discovery is unauthenticated and is never treated as trust.

## Connection sequence

The sender verifies the receiver's certificate fingerprint from the invitation during TLS before sending the secret. The server's **per-connection bootstrap callback** provisionally accepts a presented client certificate without a PKI trust check, but no transfer connection is returned until the secret is valid and the local user approves the sender's displayed name and certificate fingerprint. This is not a global certificate bypass. The sender has no independent assurance of the sender name: the receiver must accept only an expected sender and can compare fingerprints out of band. The code holder is authenticated as its bearer, not as a previously trusted person. The file offer requires a second receiver approval by default. Only when the user has enabled auto-receive globally and explicitly permitted the exact approved sender certificate in the current session can that offer be automatically approved. The transfer engine never sends bytes before a positive decision. Both certificates prove private-key possession in mutual TLS. No plaintext fallback exists; TLS 1.2 is allowed for compatibility alongside 1.3.

```mermaid
sequenceDiagram
 participant R as Receiver
 participant S as Sender
 R-->>S: Private invitation (address, TLS pin, random secret)
 S->>R: TLS; sender pins receiver certificate
 S->>R: Invitation secret over TLS
 R->>R: Check one-use secret and ask user about sender
 R-->>S: Connection accepted or rejected
 S->>R: File metadata offer
 R->>R: Ask user to accept files
 R-->>S: Accept or reject
 S->>R: If accepted, stream data and SHA-256
 R->>R: Verify and commit without overwrite
 R-->>S: File and transfer acknowledgements
```

Control frames, counts and total size are bounded. MIME is not trusted and incoming names cannot contain paths. Hashes check transfer integrity, not file safety. A lost acknowledgement can leave a verified file at the receiver while the sender reports failure. In a batch, earlier verified files remain if later files fail. Cache copies and partial files are removed on ordinary cleanup but can survive process termination or filesystem failure. Files are not executed automatically. The current UI does not delete completed received files. Avoid shared/untrusted app-data directories.

Android requests INTERNET and ACCESS_NETWORK_STATE, with no broad storage permission, notifications or background service. Discovery uses Android NsdManager; Windows uses the managed mDNS implementation from Shiny.Net.Discovery. The system picker is used. Received files remain in private app storage and are offered via the system share sheet, not saved directly to a public Downloads folder. Windows installer does not change firewall rules; users may need to allow Private network access explicitly. Keep both apps open. Guest/client isolation, VPN, multicast restrictions or firewalls can prevent connectivity. There is no relay or cloud, but manual code can contain an arbitrary address; the UI does not enforce LAN-only routing. Never accept untrusted codes. No telemetry is implemented.

## Session-only automatic reception

The toggle is off on startup; enabling it alone does not trust anyone. On a valid one-time invitation, the receiver must explicitly approve the sender certificate and separately opt in for that fingerprint. While the toggle remains enabled, later offers from that same certificate can be accepted without another prompt. The setting can be switched off or all session approvals cleared at any time. Restarting either device changes its ephemeral certificate; stored approvals are in memory only and do not survive receiver restart. Approving automatic reception for a sender also allows the current file offer automatically; users should opt in only for a sender they trust. No files are executed. Both devices still need a new one-use invitation code for each transfer, a reachable route, and the receiver must remain in its foreground waiting mode. A malicious LAN advertisement cannot grant trust.

Dependency: Shiny.Net.Discovery 6.5.2, MIT, published 2026-10-31, maintained at https://github.com/shinyorg/shiny; it uses Android NsdManager and a managed mDNS responder on Windows. Its package dependencies include Shiny.Core and platform libraries. The full dependency graph and platform behavior still require review; this is experimental software.

## iOS prototype and alternative address

An unsigned iOS simulator target has been added but device installation, key lifecycle, permission prompts, and interoperability have not been validated. The iOS plist declares local-network usage and `_hamresan._tcp` Bonjour browsing; no iOS background-receive claim is made. The optional sender-side IP override does not change the invitation's port, certificate pin or secret: TLS must still match the same receiver before the secret is transmitted. It does not create a route through a VPN, firewall, NAT, or isolated network.

The Android app directly pins matching Xamarin.AndroidX Core/Collection/Lifecycle package versions to avoid mixing incompatible dependency versions required by MAUI and Shiny. AndroidX is distributed under Apache-2.0. The Android package currently targets Android 8 (API 26) or newer. The CI APK is signed with a temporary debug key, not a production signing key.
