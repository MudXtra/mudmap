"""Launcher for the graphify MCP child inside the mudmaps container.

MudMaps.Server starts it as `/opt/graphify/bin/python -I -B /opt/graphify/run.py` with an explicit environment
(GRAPHIFY_API_KEY, GRAPHIFY_GRAPH, GRAPHIFY_PORT, ...). graphify itself runs unmodified; this file only hardens the
process before handing over to graphify's own Streamable HTTP server on 127.0.0.1.

Order: oom_score_adj, then the data rlimit, then the Landlock probe, then serve.
"""
import ctypes
import os
import resource
import sys

MIN_KEY_BYTES = 32
# graphify's measured worst case is ~1.8 GiB RSS; RLIMIT_DATA counts private writable mappings (a little above RSS),
# so the ceiling sits above that and below the container's 3 GiB limit: graphify fails with MemoryError and exits
# before the cgroup has to pick a victim.
DATA_LIMIT_BYTES = 2560 * 1024 * 1024


def fail(message: str, code: int = 2) -> None:
    print(f"run.py: {message}", file=sys.stderr, flush=True)
    sys.exit(code)


def read_config() -> tuple[str, str, int]:
    key = os.environ.get("GRAPHIFY_API_KEY", "")
    if not key.strip() or len(key.encode()) < MIN_KEY_BYTES:
        fail("GRAPHIFY_API_KEY missing, blank or shorter than 32 bytes; refusing to serve without a key")
    graph = os.environ.get("GRAPHIFY_GRAPH", "")
    if not graph:
        fail("GRAPHIFY_GRAPH missing")
    try:
        port = int(os.environ.get("GRAPHIFY_PORT", ""))
    except ValueError:
        fail("GRAPHIFY_PORT missing or not a number")
    if not 1024 <= port <= 65535:
        fail("GRAPHIFY_PORT out of range")
    return key, graph, port


def raise_oom_score() -> None:
    # Raising the score needs no capability; it makes graphify the OOM victim before the map.
    try:
        with open("/proc/self/oom_score_adj", "w") as f:
            f.write("1000")
    except OSError as e:
        print(f"run.py: could not set oom_score_adj: {e.strerror}", file=sys.stderr, flush=True)


def limit_data() -> None:
    soft, hard = resource.getrlimit(resource.RLIMIT_DATA)
    target = DATA_LIMIT_BYTES if hard == resource.RLIM_INFINITY else min(DATA_LIMIT_BYTES, hard)
    resource.setrlimit(resource.RLIMIT_DATA, (target, hard))


def landlock_abi() -> int:
    """Landlock ABI version, or -1 when the kernel does not offer it (the current host kernel does not list it)."""
    try:
        libc = ctypes.CDLL(None, use_errno=True)
        # landlock_create_ruleset(NULL, 0, LANDLOCK_CREATE_RULESET_VERSION) returns the ABI version.
        return int(libc.syscall(444, None, ctypes.c_size_t(0), ctypes.c_uint32(1)))
    except Exception:
        return -1


def main() -> None:
    key, graph, port = read_config()
    raise_oom_score()
    limit_data()
    abi = landlock_abi()
    # Not applied: the target kernel has no Landlock, so a ruleset here would be untested code on the start path.
    # If this ever logs an ABI number, add filesystem and network rules before relying on it.
    print(f"landlock: {'unavailable' if abi < 0 else f'abi={abi} (not applied)'}", file=sys.stderr, flush=True)

    from graphify.serve import serve_http

    serve_http(graph, host="127.0.0.1", port=port, api_key=key, stateless=True, json_response=True)


if __name__ == "__main__":
    main()
