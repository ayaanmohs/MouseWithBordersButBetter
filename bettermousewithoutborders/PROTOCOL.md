# Input Forwarder Protocol (v0.1-draft)

This document defines the message shapes and behaviors for forwarding laptop input events to a Windows PC while preventing the cursor/keyboard from escaping to the laptop when locked.

## Transport
- Default: TCP on port 49152 (configurable), one connection per sender.
- Encoding: newline-delimited JSON (NDJSON), UTF-8. Each line is one message object.
- Security: pre-shared secret with HMAC-SHA256 on every message (`hmac` field) using message body bytes. Upgrade path to TLS: wrap the TCP listener in TLS; message schema stays the same.
- Heartbeat: sender sends `ping` every 2s; receiver responds with `pong`.

## Modes
- `locked`: sender captures input and suppresses local handling; forwards to receiver.
- `local`: sender stops forwarding and stops suppression.
- Hotkey toggles mode locally; receiver receives `mode` message for UI.

## Message schema (top-level)
```json
{
  "type": "key|mouse|mode|ping|pong|hello|goodbye|status",
  "seq": 123,               // uint64 sequence, monotonically increasing
  "ts": 1700000000,         // unix ms
  "payload": { ... },       // type-specific
  "hmac": "hexstring"       // HMAC-SHA256(secret, bytes before hmac)
}
```

### hello
```json
{ "type": "hello", "payload": { "client": "laptop-1", "secret": "psk-id" } }
```
Receiver validates `secret` against configured value.

### key
```json
{
  "type": "key",
  "payload": {
    "vk": 0x41,          // virtual-key code
    "scan": 0x1E,        // scan code
    "isDown": true,      // true=keydown, false=keyup
    "modifiers": { "alt": false, "ctrl": false, "shift": false, "win": false }
  }
}
```

### mouse
```json
{
  "type": "mouse",
  "payload": {
    "dx": 12, "dy": -3,               // relative movement
    "buttons": { "left": "up|down", "right": "none|up|down", "middle": "none|up|down", "x1": "...", "x2": "..." },
    "wheel": { "vertical": 0, "horizontal": 0 } // +120/-120 multiples
  }
}
```

### mode
```json
{ "type": "mode", "payload": { "state": "locked|local" } }
```

### status
Receiver to sender (optional UI info)
```json
{ "type": "status", "payload": { "connected": true, "latencyMs": 12 } }
```

## Failure handling
- If receiver drops connection, sender falls back to local mode and surfaces a toast.
- If HMAC fails, receiver closes connection.
- Sequence gaps can be logged; receiver should be tolerant and process best-effort.

## Extensibility
- Add fields, never remove.
- Use feature flags inside payload when extending behaviors.

