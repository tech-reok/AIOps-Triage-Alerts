#!/bin/bash

N8N_WEBHOOK_URL="https://n8n.tech-reok.dev/webhook/container-alert"

echo "Iniciando monitor avanzado de eventos de Docker..."

# Filtramos: caídas (die), reinicios (restart), falta de memoria (oom) y fallos lógicos (unhealthy)
docker events \
  --filter 'event=die' \
  --filter 'event=restart' \
  --filter 'event=oom' \
  --filter 'event=health_status: unhealthy' \
  --format '{{json .}}' | while read -r event; do
    
    CONTAINER_NAME=$(echo "$event" | jq -r '.Actor.Attributes.name')
    EVENT_STATUS=$(echo "$event" | jq -r '.status')
    TIMESTAMP=$(echo "$event" | jq -r '.time')

    # Filtramos contenedores efímeros que no nos interesa monitorear
    if [[ "$CONTAINER_NAME" == *"github-runner"* || "$CONTAINER_NAME" == *"busybox"* ]]; then
        continue
    fi

    echo "⚠️ Alerta: Contenedor $CONTAINER_NAME reportó estado '$EVENT_STATUS'."

    # Construimos el payload
    PAYLOAD=$(cat <<EOF
{
  "container_name": "$CONTAINER_NAME",
  "event_type": "$EVENT_STATUS",
  "timestamp": "$TIMESTAMP"
}
EOF
)

    # Enviamos a n8n
    curl -s -X POST "$N8N_WEBHOOK_URL" \
         -H "Content-Type: application/json" \
         -d "$PAYLOAD"
done