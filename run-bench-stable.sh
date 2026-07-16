#!/usr/bin/env bash
set -e
cd D:/Develop/Trame
echo "=== BUILD ==="
dotnet build TrameBench/TrameBench.csproj -c Release > /dev/null 2>&1
DLL="D:/Develop/Trame/TrameBench/bin/Release/net8.0/TrameBench.dll"
rm -f bench-stable.log
for i in 1 2 3; do
  echo "=== RUN $i ==="
  dotnet "$DLL" single > /tmp/bench_run_$i.log 2>&1
  echo "--- run $i exit $? ---"
  # Summary-Block + Metadaten extrahieren
  awk '/^\/\/ \* Summary \*/,/^\/\/ \* Hints \*/' /tmp/bench_run_$i.log >> bench-stable.log
  echo "" >> bench-stable.log
done
echo "=== DONE ==="
