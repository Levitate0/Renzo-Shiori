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
COPY ./scripts/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh
EXPOSE 9833 9834
ENTRYPOINT ["tini", "--"]
CMD ["/app/entrypoint.sh"]