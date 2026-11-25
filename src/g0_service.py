# Muse Windows Bridge — G0 最小测试服务（T03 本地版）
# 仅用于 G0 接入验证，不连接真实电脑、不读写用户文件、不执行命令。
# 生产部署必须是 ASP.NET Core + 固定 HTTPS 域名；本文件是 ThreadingHTTPServer
# 本地探针实现，不得原样作为公网正式服务（见 docs/02 §T03）。
# 运行： MWB_G0_TEST_TOKEN=<随机token> python3 g0_service.py --port 8765
# 答案（随机码/按钮坐标）只保存在服务端内存与本地 answers 文件，不通过任何 API 返回。
import argparse, hashlib, json, os, random, secrets, string, sys, time, uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:
    Image = None

TOKEN = os.environ.get("MWB_G0_TEST_TOKEN", "")
ANSWERS_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "evidence", "g0", "image_answers.jsonl")
ANSWERS_PATH = os.path.normpath(ANSWERS_PATH)
MAX_ECHO = 16 * 1024
MAX_JSON = 64 * 1024
COLORS = [(220, 60, 60), (60, 140, 220), (60, 180, 90), (240, 180, 40)]

def _json(handler, code, obj):
    body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
    handler.send_response(code)
    handler.send_header("Content-Type", "application/json; charset=utf-8")
    handler.send_header("Content-Length", str(len(body)))
    handler.end_headers()
    handler.wfile.write(body)

def _err(handler, code, err_code, message, next_action="none"):
    _json(handler, code, {"code": err_code, "message": message, "next_action": next_action})

def _auth_ok(handler):
    auth = handler.headers.get("Authorization", "")
    return TOKEN and auth == "Bearer " + TOKEN

def make_image():
    """1024x768：随机四字符码 + 四色块 + 随机位置 TEST 按钮。返回 (png_bytes, answer)。"""
    if Image is None:
        raise RuntimeError("PIL unavailable")
    code = "".join(random.choice(string.ascii_uppercase + string.digits) for _ in range(4))
    rng = random.Random()
    # 按钮位置：x 100..824, y 500..668，宽 200 高 100
    bx, by = rng.randint(100, 824), rng.randint(500, 668)
    order = COLORS[:]
    rng.shuffle(order)
    img = Image.new("RGB", (1024, 768), (245, 245, 240))
    d = ImageDraw.Draw(img)
    # 四色块 2x2
    for i, c in enumerate(order):
        x0, y0 = (i % 2) * 512, (i // 2) * 200
        d.rectangle([x0, y0, x0 + 512, y0 + 200], fill=c)
    # 随机码大字（用默认字体放大绘制：描粗）
    try:
        font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 90)
        small = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 48)
    except Exception:
        font = ImageFont.load_default(); small = font
    d.text((512, 440), code, fill=(20, 20, 20), font=font, anchor="mm")
    d.rectangle([bx, by, bx + 200, by + 100], fill=(30, 30, 30), outline=(255, 255, 255), width=4)
    d.text((bx + 100, by + 50), "TEST", fill=(255, 255, 255), font=small, anchor="mm")
    import io
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    data = buf.getvalue()
    answer = {"code": code, "button_center": [bx + 100, by + 50],
              "button_rect": [bx, by, bx + 200, by + 100],
              "color_order": [list(c) for c in order]}
    return data, answer

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, fmt, *args):
        sys.stderr.write("%s %s\n" % (time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), fmt % args))
    def _read_body(self):
        n = int(self.headers.get("Content-Length") or 0)
        if n > MAX_JSON:
            return None
        return self.rfile.read(n) if n else b""
    def do_GET(self):
        p = urlparse(self.path).path
        if p == "/healthz":
            return _json(self, 200, {"status": "ok", "service": "mwb-g0-test", "version": "0.1.0-local"})
        if p in ("/g0/challenge", "/g0/image", "/g0/forbidden-device"):
            if not _auth_ok(self):
                return _err(self, 401, "UNAUTHORIZED", "missing or invalid bearer token", "reauthorize")
            if p == "/g0/forbidden-device":
                return _err(self, 403, "FORBIDDEN_DEVICE", "no access to this device", "none")
            if p == "/g0/challenge":
                return _json(self, 200, {"request_id": str(uuid.uuid4()),
                                          "nonce": secrets.token_hex(16),
                                          "issued_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())})
            # /g0/image
            data, answer = make_image()
            oid = str(uuid.uuid4())
            rec = {"observation_id": oid, "captured_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                   "width": 1024, "height": 768, "sha256": hashlib.sha256(data).hexdigest(),
                   "byte_length": len(data), "answer": answer}
            os.makedirs(os.path.dirname(ANSWERS_PATH), exist_ok=True)
            with open(ANSWERS_PATH, "a", encoding="utf-8") as f:
                f.write(json.dumps(rec, ensure_ascii=False) + "\n")
            self.send_response(200)
            self.send_header("Content-Type", "image/png")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("X-Observation-Id", oid)
            self.send_header("X-Sha256", rec["sha256"])
            self.end_headers()
            self.wfile.write(data)
            return
        return _err(self, 404, "NOT_FOUND", "unknown path", "none")
    def do_POST(self):
        p = urlparse(self.path).path
        if p != "/g0/echo":
            return _err(self, 404, "NOT_FOUND", "unknown path", "none")
        if not _auth_ok(self):
            return _err(self, 401, "UNAUTHORIZED", "missing or invalid bearer token", "reauthorize")
        raw = self._read_body()
        if raw is None:
            return _err(self, 413, "TOO_LARGE", "request too large", "none")
        if self.headers.get("Content-Type", "").split(";")[0].strip() != "application/json":
            return _err(self, 400, "BAD_CONTENT_TYPE", "expected application/json", "none")
        try:
            obj = json.loads(raw.decode("utf-8"))
        except Exception:
            return _err(self, 400, "BAD_JSON", "invalid JSON", "none")
        if not isinstance(obj, dict) or set(obj.keys()) != {"run_id", "message", "nonce"}:
            return _err(self, 400, "BAD_SCHEMA", "expected exactly run_id, message, nonce", "none")
        if not all(isinstance(obj[k], str) for k in obj) or len(raw) > MAX_ECHO:
            return _err(self, 400, "BAD_SCHEMA", "field type or size invalid", "none")
        return _json(self, 200, {"request_id": str(uuid.uuid4()), "run_id": obj["run_id"],
                                  "message": obj["message"], "nonce": obj["nonce"], "echo": True})

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--host", default="127.0.0.1")
    a = ap.parse_args()
    if not TOKEN:
        print("MWB_G0_TEST_TOKEN is required (test-only token, rotate per run)", file=sys.stderr); sys.exit(2)
    print(f"g0 test service on http://{a.host}:{a.port} (local only, NOT production)", flush=True)
    ThreadingHTTPServer((a.host, a.port), Handler).serve_forever()
