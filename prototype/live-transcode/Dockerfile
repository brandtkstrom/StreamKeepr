# syntax=docker/dockerfile:1
# Disposable prototype for issue #147. Not production code. Do not merge.
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive \
    NVIDIA_DRIVER_CAPABILITIES=video,utility \
    NVIDIA_VISIBLE_DEVICES=all

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg \
        mesa-va-drivers \
        vainfo \
        python3 \
        curl \
        ca-certificates \
    && if [ "$(dpkg --print-architecture)" = "amd64" ]; then \
         apt-get install -y --no-install-recommends \
           intel-media-va-driver \
           i965-va-driver; \
       fi \
    && rm -rf /var/lib/apt/lists/*

COPY stitch.py inside.sh /usr/local/bin/
RUN chmod +x /usr/local/bin/stitch.py /usr/local/bin/inside.sh

WORKDIR /work
ENTRYPOINT ["/usr/local/bin/inside.sh"]
