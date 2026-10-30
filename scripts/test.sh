#!/bin/bash
set -euo pipefail
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$PROJECT_ROOT"
# FIX: фиксированный порт 18876 мог быть занят другим процессом — проверки либо
# падали, либо подключались к чужому сервису. Теперь порт выбирается свободный.
TEST_PORT="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')"
export TEST_PORT
mkdir -p build/tests
swiftc -swift-version 5 -parse-as-library -O -module-cache-path build/tests/cache \
  Sources/Core.swift Sources/Privacy.swift Tests/CoreTests.swift -o build/tests/CoreTests
python3 Tests/server.py &
TEST_SERVER_PID=$!
trap 'kill "$TEST_SERVER_PID" 2>/dev/null || true' EXIT
for ATTEMPT in 1 2 3 4 5; do
  if curl --silent --fail "http://127.0.0.1:$TEST_PORT/normal" >/dev/null; then break; fi
  sleep 1
done
build/tests/CoreTests
