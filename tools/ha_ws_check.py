import sys, ssl, socket, json, base64, os, struct
cfg = sys.argv[1]
vals = {}
for line in open(cfg, encoding="utf-8"):
    if "=" in line:
        k, v = line.split("=", 1); vals[k.strip()] = v.strip().strip('"')
url = vals["HA_HOSTNAME"]; token = vals["HA_TOKEN"]
scheme, rest = url.split("://", 1)
host, _, port = rest.partition(":"); port = int(port or (443 if scheme == "https" else 80))
raw = socket.create_connection((host, port), timeout=20)
sock = ssl.create_default_context().wrap_socket(raw, server_hostname=host) if scheme == "https" else raw
key = base64.b64encode(os.urandom(16)).decode()
sock.sendall((f"GET /api/websocket HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
              f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
buf = b""
while b"\r\n\r\n" not in buf: buf += sock.recv(4096)
head, buf = buf.split(b"\r\n\r\n", 1)
assert b" 101 " in head.split(b"\r\n")[0], head[:200]
def recv_exact(n):
    global buf
    while len(buf) < n:
        chunk = sock.recv(65536)
        if not chunk: raise EOFError
        buf += chunk
    out, buf = buf[:n], buf[n:]; return out
def recv_msg():
    b1, b2 = recv_exact(2); ln = b2 & 0x7F
    if ln == 126: ln = struct.unpack(">H", recv_exact(2))[0]
    elif ln == 127: ln = struct.unpack(">Q", recv_exact(8))[0]
    data = recv_exact(ln)
    return json.loads(data.decode())
def send_msg(obj):
    payload = json.dumps(obj).encode(); mask = os.urandom(4)
    hdr = bytes([0x81])
    if len(payload) < 126: hdr += bytes([0x80 | len(payload)])
    elif len(payload) < 65536: hdr += bytes([0x80 | 126]) + struct.pack(">H", len(payload))
    else: hdr += bytes([0x80 | 127]) + struct.pack(">Q", len(payload))
    sock.sendall(hdr + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))
m = recv_msg(); print("server:", m.get("type"), m.get("ha_version"))
send_msg({"type": "auth", "access_token": token})
m = recv_msg(); print("auth:", m.get("type"), m.get("ha_version", m.get("message")))
send_msg({"id": 1, "type": "auth/current_user"})
m = recv_msg(); u = m.get("result") or {}
print("current_user:", {"name": u.get("name"), "is_admin": u.get("is_admin"), "is_owner": u.get("is_owner"), "credentials": [c.get("auth_provider_type") for c in u.get("credentials", [])], "mfa": u.get("mfa_modules") and len(u["mfa_modules"])}, "| error:", m.get("error"))
send_msg({"id": 2, "type": "config/auth/list"})
m = recv_msg()
if m.get("success"):
    for us in m["result"]:
        print("user:", {"name": us.get("name"), "is_admin": us.get("is_admin"), "is_owner": us.get("is_owner"), "is_active": us.get("is_active"), "system_generated": us.get("system_generated"), "group": us.get("group_ids")})
else:
    print("config/auth/list error:", m.get("error"))
send_msg({"id": 3, "type": "supervisor/api", "endpoint": "/info", "method": "get"})
m = recv_msg()
if m.get("success"):
    d = m["result"]; print("supervisor /info:", {k: d.get(k) for k in ["supervisor", "homeassistant", "hassos", "docker", "machine", "arch", "supported_arch", "channel", "supported", "healthy", "timezone"]})
else:
    print("supervisor/api error:", m.get("error"))
send_msg({"id": 4, "type": "supervisor/api", "endpoint": "/host/info", "method": "get"})
m = recv_msg()
if m.get("success"):
    d = m["result"]; print("host /info:", {k: d.get(k) for k in ["operating_system", "kernel", "hostname", "disk_free", "disk_total", "features"]})
else:
    print("host/info error:", m.get("error"))
send_msg({"id": 5, "type": "supervisor/api", "endpoint": "/addons", "method": "get"})

m = recv_msg()
if m.get("success"):
    print("apps:", [(a.get("slug"), a.get("version"), a.get("state")) for a in m["result"].get("addons", [])])
else:
    print("addons error:", m.get("error"))
send_msg({"id": 6, "type": "supervisor/api", "endpoint": "/core/info", "method": "get"})
m = recv_msg(); print("core /info:", {k: (m.get("result") or {}).get(k) for k in ["version","arch","machine","port","ssl"]} if m.get("success") else m.get("error"))
send_msg({"id": 7, "type": "supervisor/api", "endpoint": "/store/repositories", "method": "get"})
m = recv_msg(); print("store repositories:", [(r.get("slug"), r.get("name"), r.get("source")) for r in (m.get("result") or [])] if m.get("success") else m.get("error"))
send_msg({"id": 8, "type": "supervisor/api", "endpoint": "/services/mqtt", "method": "get"})
m = recv_msg(); print("services/mqtt:", {k: (m.get("result") or {}).get(k) for k in ["host","port","ssl","protocol","addon"]} if m.get("success") else m.get("error"))
sock.close()
