#!/usr/bin/env bash
set -euo pipefail
# Android's UI hierarchy dump can contain the invitation secret; never print or upload it.
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
read -r width height < <(adb shell wm size | tr -d '\r' | sed -nE 's/.*Physical size: ([0-9]+)x([0-9]+).*/\1 \2/p' | head -1)
: "${width:?No screen width}" "${height:?No screen height}"
found=0
switched=0
for attempt in 1 2 3 4 5; do
  adb shell uiautomator dump /sdcard/hamresan-ui.xml >/dev/null
  adb pull /sdcard/hamresan-ui.xml "$work/ui.xml" >/dev/null 2>&1
  if [ "${HAMRESAN_ENABLE_AUTO:-0}" = 1 ] && [ "$switched" -eq 0 ]; then
    if switch_coords=$(python3 - "$work/ui.xml" <<'PYSW'
import re, sys, xml.etree.ElementTree as ET
for e in ET.parse(sys.argv[1]).getroot().iter('node'):
    if e.attrib.get('class', '').endswith('Switch') and e.attrib.get('checked') == 'false':
        match = re.fullmatch(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', e.attrib.get('bounds', ''))
        if match:
            x1, y1, x2, y2 = map(int, match.groups())
            print((x1+x2)//2, (y1+y2)//2)
            sys.exit(0)
sys.exit(1)
PYSW
    ); then
      adb shell input tap $switch_coords
      switched=1
      sleep 1
    fi
  fi
  if coordinates=$(python3 - "$work/ui.xml" <<'PY'
import re, sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
for e in root.iter('node'):
    if e.attrib.get('text') == 'ساخت کد و انتظار برای دریافت':
        match = re.fullmatch(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', e.attrib.get('bounds', ''))
        if match:
            x1, y1, x2, y2 = map(int, match.groups())
            print((x1+x2)//2, (y1+y2)//2)
            sys.exit(0)
sys.exit(1)
PY
  ); then
    adb shell input tap $coordinates
    found=1
    break
  fi
  adb shell input swipe "$((width / 2))" "$((height * 8 / 10))" "$((width / 2))" "$((height * 3 / 10))" 450
  sleep 1
done
if [ "${HAMRESAN_ENABLE_AUTO:-0}" = 1 ] && [ "$switched" -ne 1 ]; then
  echo '::error::Auto-reception switch was not found or not disabled by default.'; exit 1
fi
if [ "$found" -ne 1 ]; then
  echo '::error::Receive button not found in the Android UI.'
  python3 - "$work/ui.xml" <<'PYXML'
import sys, xml.etree.ElementTree as ET
for e in ET.parse(sys.argv[1]).getroot().iter('node'):
    text = e.attrib.get('text', '')
    if text and not text.startswith('AQQ'):
        print('::notice::UI: ' + text[:100] + ' ' + e.attrib.get('bounds', ''))
PYXML
  exit 1
fi
sleep 12
adb shell uiautomator dump /sdcard/hamresan-ui.xml >/dev/null
adb pull /sdcard/hamresan-ui.xml "$work/ui.xml" >/dev/null 2>&1
if ! python3 - "$work/ui.xml" "${1:-}" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
texts = [e.attrib.get('text', '') for e in root.iter('node')]
# Check only the shape, not the actual bearer secret. 0x01, 0x04 = base64 "AQQ".
code = next((t for t in texts if t.startswith('AQQ') and len(t) >= 90), None)
if code is not None:
    if sys.argv[2]:
        import os
        fd = os.open(sys.argv[2], os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, 'w') as output:
            output.write(code)
    print('PASS: receiver produced an IPv4 one-time invitation')
    sys.exit(0)
print('::error::Receiver did not produce an invitation after its button was tapped.')
for message in ('نشانی شبکه‌ای پیدا نشد', 'عملیات انجام نشد', 'عملیات لغو شد', 'آمادهٔ دریافت'):
    if any(message in t for t in texts):
        print('::error::Receiver status contains: ' + message)
sys.exit(1)
PY
then
  exit 1
fi
