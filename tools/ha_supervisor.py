"""Call the Supervisor API of a Home Assistant instance through the Core WebSocket API (supervisor/api).

Needs an admin long-lived token. Reads HA_HOSTNAME and HA_TOKEN from a config file (default: secret.config);
the token is never printed.

  python tools/ha_supervisor.py get /addons
  python tools/ha_supervisor.py post /store/repositories '{"repository": "https://github.com/joachimcarrein/MedStock"}'
  python tools/ha_supervisor.py post /store/addons/<slug>/install --timeout 1800
  python tools/ha_supervisor.py ingress <slug> /Diag      # GET a page of an add-on through ingress

Git Bash on Windows rewrites arguments that look like paths ("/info" -> "C:/Program Files/Git/info"):
run with MSYS_NO_PATHCONV=1.
"""
import argparse, base64, json, os, socket, ssl, struct, sys, urllib.request


def load_cfg(path):
    vals = {}
    for line in open(path, encoding="utf-8"):
        if "=" in line:
            k, v = line.split("=", 1)
            vals[k.strip()] = v.strip().strip('"')
    return vals["HA_HOSTNAME"].rstrip("/"), vals["HA_TOKEN"]


class Ws:
    def __init__(self, url, token, timeout):
        scheme, rest = url.split("://", 1)
        host, _, port = rest.partition(":")
        port = int(port or (443 if scheme == "https" else 80))
        raw = socket.create_connection((host, port), timeout=timeout)
        self.sock = ssl.create_default_context().wrap_socket(raw, server_hostname=host) if scheme == "https" else raw
        key = base64.b64encode(os.urandom(16)).decode()
        self.sock.sendall((f"GET /api/websocket HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\n"
                           f"Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
        self.buf = b""
        while b"\r\n\r\n" not in self.buf:
            self.buf += self.sock.recv(4096)
        head, self.buf = self.buf.split(b"\r\n\r\n", 1)
        assert b" 101 " in head.split(b"\r\n")[0], head[:200]
        self.recv()
        self.send({"type": "auth", "access_token": token})
        m = self.recv()
        if m.get("type") != "auth_ok":
            raise SystemExit("auth failed: " + str(m.get("message")))
        self.next_id = 1

    def _exact(self, n):
        while len(self.buf) < n:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise EOFError
            self.buf += chunk
        out, self.buf = self.buf[:n], self.buf[n:]
        return out

    def _frame(self, opcode, payload):
        mask = os.urandom(4)
        n = len(payload)
        hdr = bytes([0x80 | opcode])
        if n < 126:
            hdr += bytes([0x80 | n])
        elif n < 65536:
            hdr += bytes([0x80 | 126]) + struct.pack(">H", n)
        else:
            hdr += bytes([0x80 | 127]) + struct.pack(">Q", n)
        self.sock.sendall(hdr + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))

    def send(self, obj):
        self._frame(0x1, json.dumps(obj).encode())

    def recv(self):
        data = b""
        while True:
            b1, b2 = self._exact(2)
            n = b2 & 0x7F
            if n == 126:
                n = struct.unpack(">H", self._exact(2))[0]
            elif n == 127:
                n = struct.unpack(">Q", self._exact(8))[0]
            payload = self._exact(n)
            opcode = b1 & 0x0F
            if opcode == 0x9:  # ping
                self._frame(0xA, payload)
                continue
            if opcode == 0x8:
                raise EOFError("closed")
            data += payload
            if b1 & 0x80:
                return json.loads(data.decode())

    def call(self, msg):
        msg["id"] = self.next_id
        self.next_id += 1
        self.send(msg)
        while True:
            m = self.recv()
            if m.get("id") == msg["id"] and m.get("type") == "result":
                return m

    def supervisor(self, method, endpoint, data=None, timeout=60):
        msg = {"type": "supervisor/api", "endpoint": endpoint, "method": method}
        if timeout != 60:
            msg["timeout"] = timeout
        if data is not None:
            msg["data"] = data
        return self.call(msg)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("method")
    p.add_argument("endpoint")
    p.add_argument("data", nargs="?")
    p.add_argument("--timeout", type=int, default=60)
    p.add_argument("--config", default="secret.config")
    a = p.parse_args()
    url, token = load_cfg(a.config)
    ws = Ws(url, token, timeout=a.timeout + 30)
    if a.method == "ingress":
        slug, path = a.endpoint, a.data or "/"
        info = ws.supervisor("get", f"/addons/{slug}/info")["result"]
        sess = ws.supervisor("post", "/ingress/session")["result"]["session"]
        req = urllib.request.Request(url + info["ingress_url"].rstrip("/") + path,
                                     headers={"Cookie": f"ingress_session={sess}", "Accept-Language": "nl-BE,nl;q=0.9"})
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                print("HTTP", r.status)
                sys.stdout.write(r.read().decode("utf-8", "replace"))
        except urllib.error.HTTPError as e:
            print("HTTP", e.code)
            sys.stdout.write(e.read().decode("utf-8", "replace")[:2000])
        return
    m = ws.supervisor(a.method, a.endpoint, json.loads(a.data) if a.data else None, a.timeout)
    out = m.get("result") if m.get("success") else {"error": m.get("error")}
    sys.stdout.reconfigure(encoding="utf-8")
    print(json.dumps(out, indent=1, ensure_ascii=False) if not isinstance(out, str) else out)


if __name__ == "__main__":
    main()
