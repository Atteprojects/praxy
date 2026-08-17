#!/usr/bin/env sh
# Backs up the praxy system schema and every px_<database> schema into timestamped custom-format
# pg_dump archives (architecture.md §10, docs/self-host.md). Safe to run against a live instance —
# each pg_dump runs in its own consistent snapshot transaction, no downtime required for backup.
#
# Usage: ./backup.sh [output-dir]   (default: backups/<UTC timestamp>)
set -eu
cd "$(dirname "$0")"

OUT="${1:-backups/$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$OUT"

echo "Backing up praxy system schema -> $OUT/praxy.dump"
docker compose exec -T postgres pg_dump -U praxy -d praxy -n praxy -F c > "$OUT/praxy.dump"

schemas=$(docker compose exec -T postgres psql -U praxy -d praxy -tAc \
  "select schema_name from information_schema.schemata where schema_name like 'px\_%'")

for schema in $schemas; do
  # psql -tAc output can carry a trailing \r under some locales/terminals — strip it.
  schema=$(printf '%s' "$schema" | tr -d '\r')
  [ -n "$schema" ] || continue
  echo "Backing up database schema $schema -> $OUT/$schema.dump"
  docker compose exec -T postgres pg_dump -U praxy -d praxy -n "$schema" -F c > "$OUT/$schema.dump"
done

echo "Backup complete: $OUT"
echo "Restore with: ./restore.sh $OUT"
