# rtltcp_v2 – Protocol Specification

Status: **Draft 0.1** · Applies to: rtltcp.server ≥ 0.1, clients (e.g. home.radio)

rtltcp_v2 extends the classic `rtl_tcp` protocol (here called **v1**) with:

1. clear status and error messages from the server (device not ready, busy, device lost/restored …),
2. optional user authentication,
3. full backward compatibility in both directions:
   - a **v1 client** talking to an rtltcp.server behaves exactly as with the classic `rtl_tcp`
     (as long as the client needs no authentication, see §6),
   - a **v2 client** talking to a classic `rtl_tcp` server detects this and falls back to v1.

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
- Connection closed without data → server refused (busy / auth required / not ready); v2 clients should show a generic error.

---

## 3. v2 session

### 3.1 Server header (12 bytes)

```
char[4] "RTL2"
u16     protocol_version   (1)
u16     flags              (bit 0: auth required for this client)
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

The server only sends IQ frames after the session is **authorized** (§4.2) and the client
**owns** the device (§4.3).

---

## 4. JSON messages (frame type 0x02)

Every message is an object with a `"type"` field. Unknown fields must be ignored; unknown
message types must be ignored.

### 4.1 `server_info` (S → C, always first)

```json
{
  "type": "server_info",
  "server": "rtltcp.server",
  "version": "0.1.0",
  "device": { "name": "Dach-Antenne", "serial": "00000001" },
  "auth": "required"            // or "none"
}
```

### 4.2 Authentication

Only when `auth == "required"`. Mechanism: **SCRAM-SHA-256** style (RFC 7677 math, own JSON
framing). The password is never transmitted and the server stores no password-equivalent.

```
C → S  { "type": "auth_start",     "user": "jay", "cnonce": "<base64, 18+ bytes>" }
S → C  { "type": "auth_challenge", "nonce": "<cnonce + snonce, base64>",
         "salt": "<base64>", "iterations": 100000 }
C → S  { "type": "auth_proof",     "proof": "<base64>" }
S → C  { "type": "auth_ok",        "signature": "<base64>" }     // server signature, client should verify
   or  { "type": "error", "code": "AUTH_FAILED", "message": "Authentication failed" }  → server closes
```

Computation (all HMAC/H = SHA-256):

```
SaltedPassword = PBKDF2-HMAC-SHA256(password, salt, iterations)
ClientKey      = HMAC(SaltedPassword, "Client Key")
StoredKey      = H(ClientKey)
ServerKey      = HMAC(SaltedPassword, "Server Key")
AuthMessage    = user + "," + cnonce + "," + nonce + "," + salt + "," + iterations   (base64 strings, decimal iterations)
ClientProof    = ClientKey XOR HMAC(StoredKey, AuthMessage)
ServerSignature= HMAC(ServerKey, AuthMessage)
```

**Client without credentials.** If `server_info.auth == "required"` but the user has not configured credentials,
the client should tell the user ("Server requires authentication") and disconnect. The server enforces this anyway:
any message other than `auth_start` – or nothing within **10 s** – is answered with
`{"type":"error","code":"AUTH_REQUIRED","message":"Authentication required: …","fatal":true}` and the connection is closed.

The server stores only `salt`, `iterations`, `StoredKey`, `ServerKey` per user.
For unknown users the server answers with a deterministic fake salt (no user enumeration).
Users must be **enabled** and **permitted for this device** in the web GUI; otherwise `AUTH_FAILED`
(the reason is only logged on the server side).

Without TLS the session is authenticated but **not encrypted** (IQ data is visible on the
network). TLS is a possible future extension.

### 4.3 `status` (S → C)

Sent after authorization and whenever the state changes.

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

### 4.4 `error` (S → C)

```json
{ "type": "error", "code": "BUSY", "message": "Server already in use by 192.168.1.20",
  "fatal": true, "client": "192.168.1.20" }
```

| code               | fatal | meaning                                                      |
|--------------------|-------|--------------------------------------------------------------|
| `BUSY`             | yes   | another client owns the device (first come, first served)    |
| `AUTH_REQUIRED`    | yes   | client tried to proceed without auth                         |
| `AUTH_FAILED`      | yes   | wrong credentials / user disabled / no permission            |
| `KICKED`           | yes   | an administrator disconnected the session                    |
| `INVALID_COMMAND`  | no    | command rejected (e.g. unsupported sample rate)              |
| `PROTOCOL_ERROR`   | yes   | malformed frame                                              |

After a fatal error the server closes the connection.

### 4.5 `ping` / `pong` (both directions, optional)

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

## 6. Authentication policy and trusted networks

Per server (configurable in the web GUI):

| setting            | effect                                                                         |
|--------------------|--------------------------------------------------------------------------------|
| `auth.enabled`     | `false`: nobody authenticates, v1 and v2 work for everyone                     |
| `auth.trusted_networks` | list of CIDRs; clients from these networks never need auth              |

Decision per connection:

```
if not auth.enabled or client_ip in trusted_networks:
    v1 allowed, v2 with auth = "none"
else:
    v1 → connection closed
    v2 → auth = "required"
```

Default `trusted_networks` (pre-filled when auth is enabled, editable):
`127.0.0.0/8, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, ::1/128, fc00::/7, fe80::/10`.

**Caveat:** if the router forwards external connections with source NAT (masquerading), they
arrive with an internal address and would be treated as trusted. Port forwarding normally keeps
the original address; with reverse proxies or NAT hairpinning check the log.

---

## 7. Device ownership

- One device = one TCP port = at most **one owner**.
- First connected client owns it until it disconnects ("first come, first served").
- Further clients: v2 → `BUSY` with the owner's IP, v1 → closed.
- Administrators can disconnect the owner from the web GUI (owner receives `KICKED`).

---

## 8. Example (v2, auth from public IP)

```
C: F2 52 54 4C 32                                        HELLO
S: "RTL2" 00 01 00 01 00 00 00 00                        header, auth flag set
S: [02][len]{"type":"server_info",…,"auth":"required"}
C: [02][len]{"type":"auth_start","user":"jay","cnonce":"…"}
S: [02][len]{"type":"auth_challenge",…}
C: [02][len]{"type":"auth_proof","proof":"…"}
S: [02][len]{"type":"auth_ok","signature":"…"}
S: [02][len]{"type":"status","state":"starting"}
S: [02][len]{"type":"status","state":"ready","tuner":{…}}
C: [03][00 00 00 05] 01 05 F5 E1 00                      SET_FREQUENCY 100 MHz
S: [01][00 00 40 00] <16384 bytes IQ> …
```
