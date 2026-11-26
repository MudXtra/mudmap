# syntax=docker/dockerfile:1
# Mud Maps image with the graphify MCP child.
# Build context (made by deploy/build-image.sh):
#   server/   self-contained MudMaps.Server publish
#   site/     the published map apps by segment
#   graphify/ run.py and requirements.lock (from deploy/graphify)
# Every base is pinned by digest. uv verifies the python-build-standalone download against its built-in SHA-256
# manifest, and `uv pip sync --require-hashes` refuses any wheel whose hash is not in the lock.

FROM ghcr.io/astral-sh/uv:0.11.10@sha256:bca7f6959666f3524e0c42129f9d8bbcfb0c180d847f5187846b98ff06125ead AS uv

FROM debian:bookworm-slim@sha256:734a72b3d6142a71993b4bc580ca01d8ca687f77a2c422d96a961d3376b179bc AS py
COPY --from=uv /uv /usr/local/bin/uv
ENV UV_PYTHON_INSTALL_DIR=/opt/graphify/python \
    UV_NO_CACHE=1 \
    UV_PYTHON_PREFERENCE=only-managed \
    UV_COMPILE_BYTECODE=0
COPY graphify/requirements.lock /tmp/requirements.lock
RUN uv python install 3.12.13 \
 && uv venv /opt/graphify/venv --python 3.12.13 --relocatable \
 && VIRTUAL_ENV=/opt/graphify/venv uv pip sync --require-hashes /tmp/requirements.lock \
 && PY=$(ls -d /opt/graphify/python/cpython-3.12.13-*/lib/python3.12) \
 && rm -rf "$PY"/test "$PY"/idlelib "$PY"/tkinter "$PY"/turtledemo "$PY"/ensurepip "$PY"/lib2to3 \
           /opt/graphify/python/cpython-3.12.13-*/include /opt/graphify/python/cpython-3.12.13-*/lib/*.a \
           /opt/graphify/python/cpython-3.12.13-*/lib/python3.12/config-3.12-* \
           /opt/graphify/python/cpython-3.12.13-*/bin/pip* /opt/graphify/python/cpython-3.12.13-*/bin/idle* \
           /opt/graphify/venv/lib/python3.12/site-packages/pip* \
 && find /opt/graphify -name '*.pyc' -delete \
 && /opt/graphify/venv/bin/python -m compileall -q -j0 -f /opt/graphify
COPY graphify/run.py /opt/graphify/run.py
RUN /opt/graphify/venv/bin/python -m compileall -q /opt/graphify/run.py \
 && chmod -R u=rwX,go=rX /opt/graphify

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra@sha256:bf8715422b59a65719605d4fe2b8f3f0eb96209b84cb2ed6bf3b5af7d879f148
WORKDIR /app
COPY --chown=0:0 --chmod=0755 server/ /app/
COPY --chown=0:0 site/ /site/
COPY --from=py --chown=0:0 /opt/graphify /opt/graphify
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0 \
    Urls=http://+:8080 \
    Maps__ReposRoot=/data/InternalRepos \
    Maps__Sites__colony=/site/colony \
    Maps__Sites__city=/site/city \
    Maps__Sites__islands=/site/islands \
    Maps__Sites__space=/site/space \
    Maps__Research__Python=/opt/graphify/venv/bin/python \
    Maps__Research__Launcher=/opt/graphify/run.py \
    Maps__Research__Port=8841
USER 1654:1654
EXPOSE 8080
ENTRYPOINT ["/app/MudMaps.Server"]
