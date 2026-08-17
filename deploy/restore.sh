#!/usr/bin/env sh
# Restores a backup directory produced by ./backup.sh: the praxy system schema first, then every
# px_<database> schema found alongside it. --clean --if-exists makes this safe to run against an
# instance that already has (possibly corrupted) copies of these schemas, not just an empty one.
#
# Stop the api container first (docker compose stop api) — it caches catalog metadata in memory
# per project and has no way to know a raw pg_restore just changed the tables out from under it.
# Start it again after restoring so it boots with a cold, correct cache.
#
# Usage: ./restore.sh <backup-dir>
set -eu
cd "$(dirname "$0")"

dir="${1:?usage: ./restore.sh <backup-dir>}"
[ -f "$dir/praxy.dump" ] || { echo "No praxy.dump in $dir — is this a backup.sh output directory?" >&2; exit 1; }

echo "Restoring praxy system schema from $dir/praxy.dump"
docker compose exec -T postgres pg_restore -U praxy -d praxy --clean --if-exists --no-owner < "$dir/praxy.dump"

for dump in "$dir"/px_*.dump; do
  [ -e "$dump" ] || continue
  echo "Restoring database schema $(basename "$dump" .dump) from $dump"
  docker compose exec -T postgres pg_restore -U praxy -d praxy --clean --if-exists --no-owner < "$dump"
done

echo "Restore complete. Start the api container again: docker compose start api"
