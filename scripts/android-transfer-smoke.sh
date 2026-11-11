#!/usr/bin/env bash
set -euo pipefail
# The invitation is a bearer secret. Do not echo, upload, or retain it.
work=$(mktemp -d)
chmod 700 "$work"
port=''
sender_pid=''
cleanup() {
  if [ -n "$sender_pid" ]; then kill "$sender_pid" 2>/dev/null || true; fi
  if [ -n "$port" ]; then adb forward --remove "tcp:$port" >/dev/null 2>&1 || true; fi
  adb shell rm -f /sdcard/hamresan-ui.xml >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT
./scripts/android-receive-smoke.sh "$work/invitation"
remote_port=$(python3 - "$work/invitation" <<'PY'
import base64, sys
code = open(sys.argv[1]).read().strip()
data = base64.urlsafe_b64decode(code + '=' * (-len(code) % 4))
if data[0] != 1 or data[1] != 4:
    raise ValueError('Expected an IPv4 invitation')
print(int.from_bytes(data[6:8], 'big'))
PY
)
port=$(adb forward tcp:0 "tcp:$remote_port" | tr -d '\r')
python3 - "$work/invitation" "$work/forwarded" "$port" <<'PY'
import base64, os, sys
code = open(sys.argv[1]).read().strip()
data = bytearray(base64.urlsafe_b64decode(code + '=' * (-len(code) % 4)))
# Only the test route is rewritten; the certificate pin and secret remain identical.
data[2:6] = bytes([127, 0, 0, 1])
data[6:8] = int(sys.argv[3]).to_bytes(2, 'big')
encoded = base64.urlsafe_b64encode(data).decode().rstrip('=')
fd = os.open(sys.argv[2], os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
with os.fdopen(fd, 'w') as output: output.write(encoded)
PY

dotnet run --project tests/HamResan.Android.Integration/HamResan.Android.Integration.csproj -c Release --no-build -- "$work/forwarded" > "$work/sender.log" 2>&1 &
sender_pid=$!

press_dialog() {
  local wanted="$1" coords
  for attempt in $(seq 1 25); do
    if ! kill -0 "$sender_pid" 2>/dev/null; then break; fi
    adb shell uiautomator dump /sdcard/hamresan-ui.xml >/dev/null 2>&1 || true
    adb pull /sdcard/hamresan-ui.xml "$work/ui.xml" >/dev/null 2>&1 || true
    if coords=$(python3 - "$work/ui.xml" "$wanted" <<'PY'
import re, sys, xml.etree.ElementTree as ET
try:
    root = ET.parse(sys.argv[1]).getroot()
    for e in root.iter('node'):
        if e.attrib.get('text') == sys.argv[2]:
            m = re.fullmatch(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', e.attrib.get('bounds', ''))
            if m:
                x1, y1, x2, y2 = map(int, m.groups())
                print((x1+x2)//2, (y1+y2)//2)
                sys.exit(0)
except (OSError, ET.ParseError):
    pass
sys.exit(1)
PY
    ); then
      adb shell input tap $coords
      return 0
    fi
    sleep 1
  done
  echo "::error::Expected receiver approval dialog was not observed."
  cat "$work/sender.log" || true
  return 1
}
press_dialog 'قبول'
press_dialog 'دریافت'
if ! wait "$sender_pid"; then
  sender_pid=''
  echo '::error::Test sender failed.'
  cat "$work/sender.log" || true
  exit 1
fi
sender_pid=''
adb exec-out run-as com.hamresan.app cat files/Received/hamresan-ci.txt > "$work/received"
python3 - "$work/received" <<'PY'
from pathlib import Path
import sys
if Path(sys.argv[1]).read_bytes() != b'HamResan Android integration test\n':
    raise SystemExit('::error::Android received unexpected file content')
print('PASS: approved TLS transfer stored the exact file on the Android emulator')
PY
