# rtltcp_v2 – Protocol Specification

Status: **Draft 0.2** · Applies to: rtltcp.server ≥ 1.6, clients (e.g. home.radio)

Names: **rtl_tcp** = v1, **rtl_tcp Jay extended** = v2 (on the wire still `RTL2` / `HELLO_V2`).

rtl_tcp Jay extended (v2) extends the classic `rtl_tcp` protocol (here called **v1**) with:

1. clear status and error messages from the server (device not ready, busy, device lost/restored …),
2. full backward compatibility in both directions:
   - a **v1 client** talking to an rtltcp.server behaves exactly as with the classic `rtl_tcp`,
   - a **v2 client** talking to a classic `rtl_tcp` server detects this and falls back to v1.

There is **no client authentication**. Earlier drafts had an optional SCRAM login; it was removed in
rtltcp.server 1.6. Access over the internet is handled between rtltcp.server installations (rtl.over.internet),
clients only connect locally. Clients should always send HELLO and show which protocol the server answered with.

All multi-byte integers are **big-endian** (network byte order), as in v1.

---

## 1. Recap: v1

```
Client                                Server
  | ---------- TCP connect ----------> |
  | <--- header (12 bytes) ----------- |   "RTL0" | u32 tuner_type | u32 gain_count
  | <--- raw IQ stream --------------- |   u8 I, u8 Q, u8 I, u8 Q, …  (unsigned, 127.5 = 0)
  | ---- command (5 bytes) ----------> |   u8 cmd | u32 param   (any time, any count)
```

v1 commands used by v2 unchanged:

| cmd  | name                 | param                                  |
|------|----------------------|----------------------------------------|
| 0x01 | SET_FREQUENCY        | Hz                                     |
| 0x02 | SET_SAMPLE_RATE      | samples/s                              |
| 0x03 | SET_GAIN_MODE        | 0 = auto, 1 = manual                   |
| 0x04 | SET_GAIN             | tenths of dB                           |
| 0x05 | SET_FREQ_CORRECTION  | ppm (signed)                           |
| 0x06 | SET_IF_GAIN          | (stage << 16) \| (gain & 0xFFFF)       |
| 0x07 | SET_TEST_MODE        | 0/1                                    |
| 0x08 | SET_AGC_MODE         | 0/1 (RTL2832 digital AGC)              |
| 0x09 | SET_DIRECT_SAMPLING  | 0 = off, 1 = I, 2 = Q                  |
| 0x0A | SET_OFFSET_TUNING    | 0/1                                    |
| 0x0B | SET_RTL_XTAL         | Hz                                     |
| 0x0C | SET_TUNER_XTAL       | Hz                                     |
| 0x0D | SET_GAIN_BY_INDEX    | index into the gain table              |
| 0x0E | SET_BIAS_TEE         | 0/1                                    |

Unknown commands are ignored by servers (classic `rtl_tcp` behaves the same way).

---

## 2. Negotiation (HELLO)

A v2 client sends **immediately after connecting, before reading anything**, one 5-byte v1-shaped command:

```
u8  0xF2            HELLO_V2
u32 0x52544C32      "RTL2"
```

Because HELLO is exactly one v1 command (5 bytes), a classic server parses it cleanly and ignores it.

### Server side

After `accept()` the server waits up to **HELLO_TIMEOUT = 200 ms** for the first 5 bytes.

- Exactly `F2 52 54 4C 32` received → **v2 session** (§3).
- Anything else, or timeout → **v1 session** (§5). Bytes already received are processed as v1 commands.

### Client side

The client reads the first 4 bytes:

- `"RTL2"` → v2 session.
- `"RTL0"` → the server is classic `rtl_tcp` (or v2 disabled). Read the remaining 8 header bytes and continue as v1.
- Connection closed without data → server refused (busy / not ready); v2 clients should show a generic error.

---

## 3. v2 session

### 3.1 Server header (12 bytes)

```
char[4] "RTL2"
u16     protocol_version   (1)
u16     flags              (0; bit 0 was "auth required" in Draft 0.1, never set any more)
u32     reserved           (0)
```

Everything after the header is **framed** in both directions.

### 3.2 Frames

```
u8   type
u32  length            (payload length in bytes, max 16 MiB)
u8[] payload
```

| type | direction | payload                                                        |
|------|-----------|----------------------------------------------------------------|
| 0x01 | S → C     | IQ data, raw u8 interleaved, same format as v1                  |
| 0x02 | both      | JSON message, UTF-8 (§4)                                       |
| 0x03 | C → S     | v1 command, exactly 5 bytes (`u8 cmd \| u32 param`)            |

Receivers must skip frames with unknown `type` (read `length` bytes and discard).
IQ frames are typically 16–64 KiB; overhead is < 0.05 %.

The server only sends IQ frames while the client **owns** the device (§6) and the device is `ready` (§4.2).

---

## 4. JSON messages (frame type 0x02)

Every message is an object with a `"type"` field. Unknown fields must be ignored; unknown
message types must be ignored.

### 4.1 `server_info` (S → C, always first)

```json
{
  "type": "server_info",
  "server": "rtltcp.server",
  "version": "1.6.0",
  "device": { "name": "Dach-Antenne", "serial": "00000001" },
  "auth": "none"
}
```

`auth` is always `"none"` (kept for clients written against Draft 0.1).

### 4.2 `status` (S → C)

Sent right after `server_info` and whenever the state changes.

```json
{
  "type": "status",
  "state": "ready",
  "message": "Device ready",
  "tuner": { "type": 5, "name": "R820T", "gains": [0, 9, 14, 27, 37, …] }
}
```

`tuner.type` uses the v1 numbering (1 E4000, 2 FC0012, 3 FC0013, 4 FC2580, 5 R820T, 6 R828D).
`gains` are in tenths of dB, identical to what v1 clients get via `gain_count`.

| state              | meaning                                                     | IQ flowing |
|--------------------|-------------------------------------------------------------|------------|
| `ready`            | device open, streaming                                      | yes        |
| `starting`         | device is being opened (e.g. after idle power-down)         | no         |
| `device_not_ready` | no device with the configured serial found                  | no         |
| `device_lost`      | device disappeared during the session, reconnect in progress| no         |
| `device_restored`  | device back, settings re-applied (followed by `ready`)      | –          |

The session **stays open** in all these states; the server retries and reports changes.

### 4.3 `error` (S → C)

```json
{ "type": "error", "code": "BUSY", "message": "Server already in use by 192.168.1.20",
  "fatal": true, "client": "192.168.1.20" }
```

| code               | fatal | meaning                                                      |
|--------------------|-------|--------------------------------------------------------------|
| `BUSY`             | yes   | another client owns the device (first come, first served)    |
| `KICKED`           | yes   | an administrator disconnected the session                    |
| `INVALID_COMMAND`  | no    | command rejected (e.g. unsupported sample rate)              |
| `PROTOCOL_ERROR`   | yes   | malformed frame                                              |

After a fatal error the server closes the connection. (`AUTH_REQUIRED` / `AUTH_FAILED` from Draft 0.1 are no
longer sent.)

### 4.4 `ping` / `pong` (both directions, optional)

`{ "type": "ping", "id": 1 }` → `{ "type": "pong", "id": 1 }`

---

## 5. v1 sessions on rtltcp.server

A v1 client gets exactly the classic behaviour:

- `RTL0` header + raw IQ when the device is free and ready.
- **Busy** (another client owns the device): connection is **closed immediately**
  (classic `rtl_tcp` lets it hang). v1 has no way to transport a message.
- **Device not ready**: the server keeps the connection open and waits up to
  `v1_wait_for_device` seconds (configurable, default 10) for the device; then the header is
  sent, otherwise the connection is closed.
- **Device lost during session**: TCP stays open, IQ stream pauses, resumes after reconnect with
  the previous settings re-applied.

---

## 6. Device ownership

- One device = one TCP port = at most **one owner**.
- First connected client owns it until it disconnects ("first come, first served"). This includes clients at
  other sites that use the stick over rtl.over.internet.
- Further clients: v2 → `BUSY` with the owner (IP, or site and instance), v1 → closed.
- Administrators can disconnect the owner from the web GUI (owner receives `KICKED`).

---

## 7. Example (v2)

```
C: F2 52 54 4C 32                                        HELLO
S: "RTL2" 00 01 00 00 00 00 00 00                        header
S: [02][len]{"type":"server_info",…,"auth":"none"}
S: [02][len]{"type":"status","state":"starting"}
S: [02][len]{"type":"status","state":"ready","tuner":{…}}
C: [03][00 00 00 05] 01 05 F5 E1 00                      SET_FREQUENCY 100 MHz
S: [01][00 00 40 00] <16384 bytes IQ> …
```