FROM ubuntu:resolute

ARG TARGETPLATFORM
ARG TARGETARCH
ARG TARGETOS

# ---- System deps for Chromium/CEF (JCEF) + headless X ----
RUN apt-get update && \
 apt-get install -y --no-install-recommends software-properties-common ca-certificates && \
 add-apt-repository ppa:dotnet/backports -y && \
 apt-get update && \
 apt-get -y install --no-install-recommends -y aspnetcore-runtime-8.0 gettext-base unzip tini libicu78 gosu cron libxss1 libxext6 libxrender1 libxcomposite1 libxdamage1 libxkbcommon0 libxtst6 \
 libgtk-3-0 libjogl2-jni libgluegen2-jni libglib2.0-0t64 libnss3 libdbus-1-3 libpango-1.0-0 libcairo2 libasound2t64 \
 libatk-bridge2.0-0t64 libcups2t64 libdrm2 libgbm1 xvfb libssl3t64 libgdiplus libjxl0.11 libavif-bin libheif1 libopenjp2-7 \
 openjdk-21-jre python3 && \
 rm -rf /var/lib/apt/lists/*

ENV QT_X11_NO_MITSHM=1
RUN mkdir /tmp/.X11-unix && chmod 1777 /tmp/.X11-unix
      
ENV HOME=/config/mihon
WORKDIR /app
COPY ./bin/$TARGETPLATFORM/ .
# JVM sidecar assets: the AndroidCompat fat jar (contains the sidecar server + bundled
# compression/zstd) and the enjarify translator. Staged by build_sidecar.ps1 before the image build.
COPY ./sidecar/ /app/sidecar/
ENV RENZO_SIDECAR_JAVA=java \
    RENZO_SIDECAR_JAR=/app/sidecar/AndroidCompat-1.0-all.jar \
    RENZO_ENJARIFY_DIR=/app/sidecar/enjarify \
    RENZO_SIDECAR_LDPATH=/usr/lib/jvm/java-21-openjdk-amd64/lib \
    RENZO_SIDECAR_THREADS=32

# ── Memory budget ────────────────────────────────────────────────────────────
# Three things share the container and only one of them is the app. Measured on
# a 16G deployment mid-sweep: embedded browsers 8.3G, backend 4.5G, sidecar JVM
# 1.4G — 14.2G of 16G, after which the backend grew into the rest and the .NET
# runtime aborted with "Out of memory". Docker restarted it, and it did the same
# thing again.
#
# None of the three knew about the other two, so each default is set here
# relative to the whole:
#
#   RENZO_KCEF_MAX_LIVE   Embedded Chromium instances kept alive, ~250MB each.
#                         The catalogue sweep that opens most of them runs FOUR
#                         sources at a time, so 8 is already double the working
#                         set; the old 16 just let abandoned ones pile up.
#   RENZO_SIDECAR_XMX     The extension JVM's ceiling — unchanged, but stated
#                         here so the budget is readable in one place.
#   DOTNET_GCHeapHardLimitPercent
#                         0x32 = 50% of the cgroup limit. Without it the server
#                         GC sizes its heap from the WHOLE cgroup, so it happily
#                         grows into memory the browsers already hold and the
#                         process is aborted. With it the GC collects at its own
#                         ceiling, and the worst case becomes a recoverable
#                         OutOfMemoryException on one request instead of the
#                         runtime killing the container.
#
# All three are plain env vars: override any of them at `docker run` without
# rebuilding.
ENV RENZO_KCEF_MAX_LIVE=8 \
    RENZO_SIDECAR_XMX=1500m \
    DOTNET_GCHeapHardLimitPercent=0x32
COPY ./scripts/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh
EXPOSE 9833 9834
ENTRYPOINT ["tini", "--"]
CMD ["/app/entrypoint.sh"]