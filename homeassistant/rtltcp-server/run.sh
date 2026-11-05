#!/bin/sh
# All settings (sticks, ports, users, authentication) live in /data and are managed in the web interface.
exec /opt/rtltcp-server/rtltcp-server --data /data
