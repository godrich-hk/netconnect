#!/usr/bin/env bash
set -euo pipefail
# Invitations are bearer secrets. Do not echo, upload, or retain them.
work=$(mktemp -d); chmod 700 "$work"
port=''; sender_pid=''
cleanup() {
  if [ -n "$sender_pid" ]; then kill "$sender_pid" 2>/dev/null || true; fi
  if [ -n "$port" ]; then adb forward --remove "tcp:$port" >/dev/null 2>&1 || true; fi
  adb shell rm -f /sdcard/hamresan-ui.xml >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT
# Reset the debug emulator app, including test data and session trust.
adb shell am force-stop com.hamresan.app
adb shell pm clear com.hamresan.app >/dev/null
adb shell monkey -p com.hamresan.app -c android.intent.category.LAUNCHER 1 >/dev/null
sleep 5
HAMRESAN_ENABLE_AUTO=1 ./scripts/android-receive-smoke.sh "$work/invitation1"
remote_port=$(python3 - "$work/invitation1" <<'PY'
import base64, sys
s=open(sys.argv[1]).read().strip(); b=base64.urlsafe_b64decode(s+'='*(-len(s)%4))
if b[0] != 1 or b[1] != 4: raise ValueError('IPv4 invitation required')
print(int.from_bytes(b[6:8], 'big'))
PY
)
port=$(adb forward tcp:0 "tcp:$remote_port" | tr -d '\r')
forward_code() {
  python3 - "$1" "$2" "$port" "$remote_port" <<'PY'
import base64, os, sys
s=open(sys.argv[1]).read().strip(); b=bytearray(base64.urlsafe_b64decode(s+'='*(-len(s)%4)))
if b[0] != 1 or b[1] != 4 or int.from_bytes(b[6:8], 'big') != int(sys.argv[4]):
    raise ValueError('Unexpected invitation endpoint')
b[2:6]=bytes([127,0,0,1]); b[6:8]=int(sys.argv[3]).to_bytes(2,'big')
data=base64.urlsafe_b64encode(b).decode().rstrip('=')
# Rename after writing, so the sender never reads a partially written secret.
fd=os.open(sys.argv[2]+'.tmp',os.O_WRONLY|os.O_CREAT|os.O_TRUNC,0o600)
with os.fdopen(fd,'w') as f: f.write(data)
os.replace(sys.argv[2]+'.tmp',sys.argv[2])
PY
}
forward_code "$work/invitation1" "$work/forwarded1"
dotnet run --project tests/HamResan.Android.AutoIntegration/HamResan.Android.AutoIntegration.csproj -c Release --no-build -- "$work/forwarded1" "$work/forwarded2" "$work/first" > "$work/sender.log" 2>&1 &
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
    for e in ET.parse(sys.argv[1]).getroot().iter('node'):
        if e.attrib.get('text') == sys.argv[2]:
            m=re.fullmatch(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]',e.attrib.get('bounds',''))
            if m:
                x1,y1,x2,y2=map(int,m.groups()); print((x1+x2)//2,(y1+y2)//2); sys.exit(0)
except (OSError, ET.ParseError): pass
sys.exit(1)
PY
    ); then adb shell input tap $coords; return 0; fi
    sleep 1
  done
  echo '::error::Expected first-time consent dialog was not observed.'; cat "$work/sender.log" || true; return 1
}
press_dialog 'قبول'
press_dialog 'فعال شود'
for attempt in $(seq 1 30); do
  [ -f "$work/first" ] && break
  if ! kill -0 "$sender_pid" 2>/dev/null; then break; fi
  sleep 1
done
if [ ! -f "$work/first" ]; then
  echo '::error::The first auto-approved transfer did not complete.'; cat "$work/sender.log" || true; exit 1
fi
# Wait for a fresh one-use code. Its contents are never printed.
new_code=0
for attempt in $(seq 1 20); do
  adb shell uiautomator dump /sdcard/hamresan-ui.xml >/dev/null 2>&1 || true
  adb pull /sdcard/hamresan-ui.xml "$work/ui.xml" >/dev/null 2>&1 || true
  if python3 - "$work/ui.xml" "$work/invitation1" "$work/invitation2" <<'PY'
import os, sys, xml.etree.ElementTree as ET
try:
    old=open(sys.argv[2]).read().strip()
    texts=(e.attrib.get('text','') for e in ET.parse(sys.argv[1]).getroot().iter('node'))
    value=next((t for t in texts if t.startswith('AQQ') and len(t)>=90 and t!=old), None)
    if value:
        fd=os.open(sys.argv[3],os.O_WRONLY|os.O_CREAT|os.O_TRUNC,0o600)
        with os.fdopen(fd,'w') as f: f.write(value)
        sys.exit(0)
except (OSError, ET.ParseError): pass
sys.exit(1)
PY
  then new_code=1; break; fi
  sleep 1
done
if [ "$new_code" -ne 1 ]; then echo '::error::No fresh invitation after the first transfer.'; exit 1; fi
forward_code "$work/invitation2" "$work/forwarded2"
# No more UI confirmations are clicked: success proves both connection and file offer were auto-accepted.
if ! wait "$sender_pid"; then sender_pid=''; cat "$work/sender.log" || true; exit 1; fi
sender_pid=''
for n in 1 2; do
  adb exec-out run-as com.hamresan.app cat "files/Received/auto-ci-$n.txt" > "$work/received$n"
  python3 - "$work/received$n" "$n" <<'PY'
from pathlib import Path
import sys
if Path(sys.argv[1]).read_bytes() != f'HamResan trusted transfer {sys.argv[2]}\n'.encode():
    raise SystemExit('::error::Received file mismatch')
PY
done
echo 'PASS: explicit session trust enabled automatic reception for the same TLS identity'
