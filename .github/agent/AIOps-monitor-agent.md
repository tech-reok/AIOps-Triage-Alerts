# AIOps Container Event Monitor

This module is responsible for natively monitoring the Docker Engine daemon to detect critical infrastructure events. It captures container crashes, infinite restart loops, Out-Of-Memory (OOM) kills, and logical application failures, forwarding them to the n8n orchestrator for AI-driven Root Cause Analysis (RCA).

## 1. Event Monitor Agent (`container_monitor.sh`)

This script acts as a lightweight agent. It listens to the Docker event stream and filters only the critical events, preventing resource overhead.

Create the script at `/opt/scripts/container_monitor.sh`:

```bash
#!/bin/bash

# Configuration: Replace with your actual n8n Webhook URL
N8N_WEBHOOK_URL="[http://127.0.0.1:5678/webhook/container-alert](http://127.0.0.1:5678/webhook/container-alert)"

echo "Starting advanced Docker event monitor..."

# Filter specific events: crashes (die), restarts (restart), out of memory (oom), and logical failures (unhealthy)
docker events \
  --filter 'event=die' \
  --filter 'event=restart' \
  --filter 'event=oom' \
  --filter 'event=health_status: unhealthy' \
  --format '{{json .}}' | while read -r event; do
    
    CONTAINER_NAME=$(echo "$event" | jq -r '.Actor.Attributes.name')
    EVENT_STATUS=$(echo "$event" | jq -r '.status')
    TIMESTAMP=$(echo "$event" | jq -r '.time')

    # Ignore ephemeral containers (e.g., GitHub Actions runners or temporary build environments)
    if [[ "$CONTAINER_NAME" == *"github-runner"* \vert{}\vert{} "$CONTAINER_NAME" == *"busybox"* ]]; then
        continue
    fi

    echo "⚠️ Alert: Container $CONTAINER_NAME reported status '$EVENT_STATUS'."

    # Build the JSON payload
    PAYLOAD=$(cat <<EOF "$CONTAINER_NAME", "$EVENT_STATUS", "$N8N_WEBHOOK_URL" "$PAYLOAD" "$TIMESTAMP" "Content-Type: "container_name": "event_type": "timestamp": # ) -H -X -d -s EOF POST Send \ ``` alert application/json" curl done n8n the to webhook { }> **Note:** Ensure the script is executable by running `chmod +x /opt/scripts/container_monitor.sh` and that the `jq` package is installed (`sudo apt install jq`).

## 2. Systemd Service Configuration

To ensure the monitor runs continuously in the background and survives server reboots, it is configured as a Linux systemd service.

Create the service file at `/etc/systemd/system/docker-monitor.service`:

```ini
[Unit]
Description=Docker Events Monitor for AIOps (n8n Webhook Trigger)
After=docker.service
Requires=docker.service

[Service]
# Ensure the path matches the location of your script
ExecStart=/bin/bash /opt/scripts/container_monitor.sh
Restart=always
RestartSec=10
User=root

[Install]
WantedBy=multi-user.target