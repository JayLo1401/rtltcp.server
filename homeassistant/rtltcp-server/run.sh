#!/usr/bin/with-contenv bashio
# shellcheck shell=bash
# Sticks, stick ports, users and authentication live in /data and are managed in the web interface.
# The environment is set here explicitly: s6-overlay does not hand the Dockerfile ENV to this script.

# Requests from the HA ingress proxy count as logged in. The ingress port is chosen by the Supervisor.
export RTLTCP_INGRESS=1
RTLTCP_INGRESS_PORT="$(bashio::addon.ingress_port)"
export RTLTCP_INGRESS_PORT

# Direct access to the web interface (own login); 0 = only via Home Assistant
if bashio::config.has_value 'web_port'; then
    RTLTCP_WEB_PORT="$(bashio::config 'web_port')"
else
    RTLTCP_WEB_PORT=0
fi
export RTLTCP_WEB_PORT

# Port of the first stick, only used when /data/config.json is created
RTLTCP_STREAM_PORT="$(bashio::config 'stream_port')"
export RTLTCP_STREAM_PORT

export DOTNET_BUNDLE_EXTRACT_BASE_DIR=/data/.cache

if [ "${RTLTCP_WEB_PORT}" = "0" ]; then
    bashio::log.info "Web interface only via Home Assistant (ingress port ${RTLTCP_INGRESS_PORT})"
else
    bashio::log.info "Web interface via Home Assistant and on port ${RTLTCP_WEB_PORT}"
fi

exec /opt/rtltcp-server/rtltcp-server --data /data
