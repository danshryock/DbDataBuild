#!/usr/bin/env bash
# Throwaway local engines for the conformance suite (tests/DbDataBuild.Tests.Conformance). Needs docker.
#   scripts/test-engines.sh up      start SQL Server and PostgreSQL on loopback ports, wait until ready, print the exports
#   scripts/test-engines.sh down    stop and remove them
# Then: eval "$(scripts/test-engines.sh env)"  and  dotnet test tests/DbDataBuild.Tests.Conformance
# The suite refuses any host that is not loopback. Passwords here belong to throwaway containers only.
set -euo pipefail
MSSQL_IMAGE="${DDB_MSSQL_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
PG_IMAGE="${DDB_PG_IMAGE:-postgres:17-alpine}"
SA_PASSWORD='Ddb!Conf_7xQ2'
PG_PASSWORD='ddbconf'

port() { docker port "$1" "$2" | head -1 | sed 's/.*://'; }

case "${1:-}" in
  up)
    docker run -d --rm --name ddb-conf-mssql --label dbdatabuild-conformance=1 -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$SA_PASSWORD" -p 127.0.0.1::1433 "$MSSQL_IMAGE" >/dev/null
    docker run -d --rm --name ddb-conf-pg --label dbdatabuild-conformance=1 -e "POSTGRES_PASSWORD=$PG_PASSWORD" -p 127.0.0.1::5432 "$PG_IMAGE" >/dev/null
    for i in $(seq 1 60); do
      docker exec ddb-conf-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SA_PASSWORD" -C -Q "SELECT 1" >/dev/null 2>&1 && \
      docker exec ddb-conf-pg pg_isready -U postgres >/dev/null 2>&1 && break
      sleep 1
    done
    "$0" env
    ;;
  env)
    echo "export DBDATABUILD_TEST_MSSQL='Server=127.0.0.1,$(port ddb-conf-mssql 1433);User Id=sa;Password=$SA_PASSWORD;TrustServerCertificate=true;Encrypt=false'"
    echo "export DBDATABUILD_TEST_PG='Host=127.0.0.1;Port=$(port ddb-conf-pg 5432);Username=postgres;Password=$PG_PASSWORD'"
    ;;
  down)
    docker ps -q --filter label=dbdatabuild-conformance=1 | xargs -r docker stop >/dev/null
    ;;
  *) echo "usage: $0 up|env|down" >&2; exit 2 ;;
esac
