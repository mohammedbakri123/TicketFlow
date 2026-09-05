#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${1:-http://localhost:5024}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SAMPLES_FILE="$SCRIPT_DIR/../sample-tickets.json"

if [ ! -f "$SAMPLES_FILE" ]; then
  echo "Error: sample file not found at $SAMPLES_FILE" >&2
  exit 1
fi

echo "Loading sample tickets into $BASE_URL/tickets..."

if command -v python3 >/dev/null 2>&1; then
  python3 - <<EOF
import json, urllib.request, sys

base_url = "$BASE_URL"
with open("$SAMPLES_FILE", "r", encoding="utf-8") as f:
    tickets = json.load(f)

for ticket in tickets:
    payload = json.dumps(ticket).encode("utf-8")
    req = urllib.request.Request(
        f"{base_url}/tickets",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST"
    )
    try:
        with urllib.request.urlopen(req) as resp:
            print(f"[{resp.status}] Submitted {ticket['id']}: {ticket['subject']}")
    except urllib.error.HTTPError as e:
        print(f"[{e.code}] Failed {ticket['id']}: {e.read().decode('utf-8')}", file=sys.stderr)
EOF
elif command -v jq >/dev/null 2>&1; then
  jq -c '.[]' "$SAMPLES_FILE" | while read -r ticket; do
    tid=$(echo "$ticket" | jq -r '.id')
    status=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$BASE_URL/tickets" -H "Content-Type: application/json" -d "$ticket")
    echo "[$status] Submitted $tid"
  done
else
  echo "Error: python3 or jq is required to parse $SAMPLES_FILE" >&2
  exit 1
fi

echo "Sample tickets submission completed."
