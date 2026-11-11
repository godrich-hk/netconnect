#!/usr/bin/env bash
set -euo pipefail
# Android's UI hierarchy dump can contain the invitation secret; never print or upload it.
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
found=0
for attempt in 1 2 3 4 5; do
  adb shell uiautomator dump /sdcard/hamresan-ui.xml >/dev/null
  adb pull /sdcard/hamresan-ui.xml "$work/ui.xml" >/dev/null 2>&1
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
  adb shell input swipe 500 1600 500 650 450
  sleep 1
done
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
if ! python3 - "$work/ui.xml" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
texts = [e.attrib.get('text', '') for e in root.iter('node')]
# Check only the shape, not the actual bearer secret. 0x01, 0x04 = base64 "AQQ".
if any(t.startswith('AQQ') and len(t) >= 90 for t in texts):
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
