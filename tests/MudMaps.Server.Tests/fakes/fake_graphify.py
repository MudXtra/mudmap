"""A stand-in for graphify's Streamable HTTP server, started by GraphifyHost in the tests.

Speaks the minimum: POST /mcp with an X-API-Key check (401 otherwise), JSON responses. Like graphify it serves one
request at a time, so a slow call blocks the health probe. Every start, request and SIGTERM is appended to the log
file as one JSON line, which the tests read.

Arguments (passed through Maps:Research:ChildArgs, since the environment is fixed):
  --log FILE              where to append events (required)
  --exit-at-start         exit with code 3 straight away (restart backoff)
  --hang-initialize       never answer initialize (failed probes)
  --initialize-ok N       answer the first N initialize requests, then hang (probes fail after ready)
  --no-oom                leave oom_score_adj alone

A tools/call's behaviour is chosen by its first string argument (question, label, source, ...):
  sleep:N   answer after N seconds       sse      answer as text/event-stream
  nonjson   answer with plain text       big      answer with 300 KiB of JSON
  paths     text full of host paths      token    text with a GitHub token prefix
  errpath   a JSON-RPC error whose message and data hold paths
anything else: a small answer that starts with graphify's "Graph: <path> (N nodes)" header.
"""
import json
import os
import signal
import sys
import time
from http.server import BaseHTTPRequestHandler, HTTPServer

args = sys.argv[1:]
LOG = args[args.index("--log") + 1]
KEY = os.environ.get("GRAPHIFY_API_KEY", "")
PORT = int(os.environ["GRAPHIFY_PORT"])
GRAPH = os.environ.get("GRAPHIFY_GRAPH", "")
INITS = 0


def log(event, **fields):
    with open(LOG, "a") as f:
        f.write(json.dumps({"event": event, "t": time.time(), **fields}) + "\n")


def on_term(signum, frame):
    log("sigterm", pid=os.getpid())
    sys.exit(0)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send(self, status, body, ctype="application/json"):
        data = body if isinstance(body, bytes) else body.encode()
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        try:
            self.wfile.write(data)
        except OSError:
            pass

    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        log("request", path=self.path, headers={k: v for k, v in self.headers.items()}, body=raw.decode("utf-8", "replace"))
        if self.headers.get("X-API-Key") != KEY:
            return self.send(401, '{"error":"unauthorized"}')
        msg = json.loads(raw)
        rid = msg.get("id")
        if msg.get("method") == "initialize":
            global INITS
            INITS += 1
            if "--hang-initialize" in args or ("--initialize-ok" in args and INITS > int(args[args.index("--initialize-ok") + 1])):
                time.sleep(3600)
            return self.send(200, json.dumps({"jsonrpc": "2.0", "id": rid, "result": {
                "protocolVersion": "2025-06-18", "capabilities": {"tools": {}}, "serverInfo": {"name": "graphify", "version": "0.9.108"}}}))
        params = msg.get("params") or {}
        arguments = params.get("arguments") or {}
        first = next((v for v in arguments.values() if isinstance(v, str)), "")
        graph = (arguments.get("project_path", "") + "/graphify-out/graph.json") if "project_path" in arguments else GRAPH

        def text(t, is_error=False):
            return json.dumps({"jsonrpc": "2.0", "id": rid, "result": {"content": [{"type": "text", "text": t}], "isError": is_error}})

        if first.startswith("sleep:"):
            time.sleep(float(first[6:]))
            return self.send(200, text(f"Graph: {graph} (5 nodes) | slept"))
        if first == "sse":
            return self.send(200, "event: message\ndata: " + text("x") + "\n\n", "text/event-stream")
        if first == "nonjson":
            return self.send(200, "Internal error", "text/plain")
        if first == "big":
            return self.send(200, text("x" * (300 * 1024)))
        if first == "paths":
            return self.send(200, text(f"Graph: {graph} (7 nodes)\nfrom /home/user/x and /opt/graphify/lib/a.py and {GRAPH}\n"
                                       "NODE Foo [src=src/A.cs loc=L3 community=X]", is_error=True))
        if first == "token":
            return self.send(200, text("leaked ghp_abcdefghijklmnopqrstuvwxyz0123456789"))
        if first == "errpath":
            return self.send(200, json.dumps({"jsonrpc": "2.0", "id": rid, "error": {
                "code": -32603, "message": f"graph.json not found: {GRAPH}", "data": {"where": "/tmp/graphify/x"}}}))
        name = params.get("name")
        return self.send(200, text(f"Graph: {graph} (5 nodes) | tool={name}\nNODE Foo [src=src/A.cs loc=L3 community=X]"))


def main():
    signal.signal(signal.SIGTERM, on_term)
    if "--no-oom" not in args:
        try:
            with open("/proc/self/oom_score_adj", "w") as f:
                f.write("1000")
        except OSError:
            pass
    # The environment exactly as exec received it (Python itself may add LC_CTYPE to os.environ afterwards).
    initial = dict(kv.split("=", 1) for kv in open("/proc/self/environ").read().split("\0") if "=" in kv)
    log("start", pid=os.getpid(), env=initial, argv=sys.argv, flags=sys.flags.isolated)
    print("landlock: unavailable", file=sys.stderr, flush=True)
    print("some stdout line", flush=True)
    if "--exit-at-start" in args:
        sys.exit(3)
    HTTPServer(("127.0.0.1", PORT), Handler).serve_forever()


main()
