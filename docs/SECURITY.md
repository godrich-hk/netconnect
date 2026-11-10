# Security and current limitations

**Experimental; not security reviewed and not ready for sensitive data or general release.** The app does not yet implement the originally required persistent per-install hardware/platform-secured identity, trusted-peer storage, SAS comparison, or revoked/changed identity warning. It generates ephemeral self-signed certificates for a session. A new code and consent are required each time. No auto-accept is implemented.

The invitation includes a receiver address, port, SHA-256 certificate fingerprint, and random 256-bit bearer secret. It must be shared privately through an authenticated out-of-band channel. Anyone who obtains it can attempt to use it; clipboard and communications used to share it can leak it. A short PIN without a reviewed PAKE protocol would be vulnerable to offline guessing, so this implementation uses a long random code. It is not a persistent trust relationship. The in-memory verifier expires after ten minutes, consumes a valid code once, and loses it on restart. An attacker can deny service by connecting first or consuming a leaked code; there is no attempt rate limit. No invitation data is sent in discovery (which is not implemented).

## Connection sequence

The sender verifies the receiver's certificate fingerprint from the invitation during TLS before sending the secret. The server's **per-connection bootstrap callback** provisionally accepts a presented client certificate without a PKI trust check, but no transfer connection is returned until the secret is valid and the local user approves the sender's displayed name and certificate fingerprint. This is not a global certificate bypass. The sender has no independent assurance of the sender name: the receiver must accept only an expected sender and can compare fingerprints out of band. The code holder is authenticated as its bearer, not as a previously trusted person. The file offer then requires a second receiver approval; transfer bytes are never sent by the engine before this decision. Both certificates prove private-key possession in mutual TLS. No plaintext fallback exists; TLS 1.2 is allowed for compatibility alongside 1.3.

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

Android requests INTERNET and ACCESS_NETWORK_STATE, with no broad storage permission, multicast, notifications or background service. The system picker is used. Received files remain in private app storage and are offered via the system share sheet, not saved directly to a public Downloads folder. Windows installer does not change firewall rules; users may need to allow Private network access explicitly. Keep both apps open. Guest/client isolation, VPN, multicast restrictions or firewalls can prevent connectivity. There is no relay or cloud, but manual code can contain an arbitrary address; the UI does not enforce LAN-only routing. Never accept untrusted codes. No telemetry is implemented.
