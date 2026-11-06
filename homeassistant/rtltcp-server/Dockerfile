ARG BUILD_FROM
FROM ${BUILD_FROM}

ARG BUILD_ARCH
ARG BUILD_VERSION
ARG RTLTCP_BASE

# libusb for the stick, OpenSSL for .NET crypto (login hashes)
RUN apt-get update \
    && apt-get install -y --no-install-recommends libusb-1.0-0 libssl3 ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*

# The released Linux build of exactly this add-on version, checksum-verified
RUN set -eu; \
    case "${BUILD_ARCH}" in \
        aarch64) RID=linux-arm64 ;; \
        amd64) RID=linux-x64 ;; \
        armv7) RID=linux-arm ;; \
        *) echo "unsupported architecture ${BUILD_ARCH}" >&2; exit 1 ;; \
    esac; \
    FILE="rtltcp-server-${BUILD_VERSION}-${RID}.tar.gz"; \
    URL="${RTLTCP_BASE}/linux/release/${BUILD_VERSION}/${FILE}"; \
    curl -fsSL -o /tmp/rtltcp.tar.gz "${URL}"; \
    echo "$(curl -fsSL "${URL}.sha256")  /tmp/rtltcp.tar.gz" | sha256sum -c -; \
    mkdir -p /opt/rtltcp-server; \
    tar -xzf /tmp/rtltcp.tar.gz -C /opt/rtltcp-server; \
    rm /tmp/rtltcp.tar.gz; \
    /opt/rtltcp-server/rtltcp-server --version

# Ports and environment are set in run.sh (from the add-on options and the Supervisor's ingress port)
COPY run.sh /
RUN chmod a+x /run.sh
CMD [ "/run.sh" ]

LABEL io.hass.version="${BUILD_VERSION}" io.hass.type="addon" io.hass.arch="${BUILD_ARCH}"
