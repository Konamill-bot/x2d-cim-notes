# X2D WiFi Network Observations

Brief notes on what the X2D's WiFi hotspot looks like from the network layer.
Useful as a starting point for anyone considering an "alternative client" to
Phocus that talks directly to the camera over WiFi.

## Setup

- X2D enables its own WiFi access point (SSID format `Hasselblad-XXXXXX`)
- Default camera IP: `192.168.2.1`
- Default DHCP-issued client IP: `192.168.2.x` (commonly `.100`)
- PC must connect to that hotspot to reach the camera (loses internet access while connected)

## Port scan result (TCP, full sweep of famous + 1500–9999 + dynamic samples)

- **Only TCP port 80 is open** on `192.168.2.1`
- All other scanned TCP ports refused connection or timed out
- The scan was run with the camera powered on, WiFi connected, and Phocus
  not actively tethered. Re-running with Phocus connected may or may not
  open additional dynamic ports — not yet verified.

## Behavior of port 80

Although TCP-listening, port 80 is **not HTTP**:

- A browser request to `http://192.168.2.1/` returns `ERR_INVALID_RESPONSE`
- `HEAD / HTTP/1.0` → no bytes returned
- `OPTIONS * RTSP/1.0` → no bytes returned
- Passive read after connect → no bytes returned

The server accepts the TCP handshake but does not send any bytes until the
client speaks first in some protocol-specific format. This is consistent
with a custom binary RPC protocol that has a magic-byte prefix or
authentication handshake that the camera expects.

## Implication for an "alternative client" approach

In principle, anyone who can:

1. Capture Phocus's traffic to the camera (Wireshark, USBPcap, etc.)
2. Identify the wire format of the first messages
3. Replay or reimplement those messages

…could in theory write a client that speaks directly to the camera, bypassing
Phocus on the PC. That work would be lawful (it is network protocol
documentation of a device the owner controls).

However, even a successful alternative client almost certainly hits the same
capability-gating wall observed via IPC: the camera's reported
`focusModeRange` does not include AF-C regardless of which client is talking.
The decision to expose AF-C is made by the camera firmware, not by Phocus
or by any external client. So an alternative client does not, on its own,
unlock AF-C.

Estimated effort to build a viable alternative client: 2–4 weeks of focused
work, with a high probability that the end result reaches the same firmware
gate that the IPC route reaches.

## What this rules out

- HTTP-based shortcut: the camera is not a small REST/JSON service
- Quick banner-grab identification: the server is silent until the right
  request arrives
- Lazy "just point a browser at it" research: nothing renders

## What this leaves open (descriptive, not adversarial)

- Wireshark / USBPcap capture of an actual Phocus session would reveal
  the wire format unambiguously. The format itself is information about
  the protocol, not a circumvention of security.
- Cross-version captures across X2D firmware revisions could show whether
  the protocol changed between releases — useful structural information.
- None of these change the conclusion that AF-C is gated on the camera
  side, not the client side.
