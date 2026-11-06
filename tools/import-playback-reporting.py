#!/usr/bin/env python3
"""Copy Playback Reporting's history into the Zorofin plugin's play history.

Reads playback_reporting.db read-only (through a private snapshot) and sends the
rows to POST /Zorofin/Admin/Activity/Import in batches. Rows already imported are
skipped by the plugin, so running this again is safe. Nothing in Playback
Reporting is changed.

  JELLYFIN_API_KEY=... tools/import-playback-reporting.py --dry-run
  JELLYFIN_API_KEY=... tools/import-playback-reporting.py --url http://127.0.0.1:8096

Run it before turning on Play history in the plugin (and again just before, if
time has passed): plays after the plugin starts recording are left out, since
both would have recorded them.

Playback Reporting stores times in the server's local time without a zone; pass
--time-zone if this machine's zone differs from the Jellyfin server's.
"""
import argparse
import json
import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request

DEFAULT_DB = Path.home() / 'Library/Application Support/jellyfin/data/playback_reporting.db'
COLUMNS = ('RowId', 'DateCreated', 'UserId', 'ItemId', 'ItemType', 'ItemName', 'PlaybackMethod', 'ClientName', 'DeviceName', 'PlayDuration')
BATCH = 2000


def local_zone():
    if os.environ.get('TZ'):
        return os.environ['TZ']
    try:
        target = os.readlink('/etc/localtime')
        if 'zoneinfo/' in target:
            return target.split('zoneinfo/', 1)[1]
    except OSError:
        pass
    return None


def read_rows(db):
    # Never open the live database for writing; copy it to a private snapshot first.
    with tempfile.TemporaryDirectory() as scratch:
        os.chmod(scratch, 0o700)
        with sqlite3.connect(db.resolve().as_uri() + '?mode=ro', uri=True, timeout=5) as source:
            with sqlite3.connect(Path(scratch) / 'playback.db') as copy:
                source.backup(copy, pages=256, sleep=.05)
                copy.row_factory = sqlite3.Row
                have = {r[1] for r in copy.execute('PRAGMA table_info(PlaybackActivity)')}
                select = ', '.join('rowid AS RowId' if c == 'RowId' else c if c in have else f'NULL AS {c}' for c in COLUMNS)
                return [dict(r) for r in copy.execute(f'SELECT {select} FROM PlaybackActivity ORDER BY rowid')]


def send(url, key, zone, rows):
    body = json.dumps({'timeZone': zone, 'rows': rows}).encode()
    request = urllib.request.Request(url.rstrip('/') + '/Zorofin/Admin/Activity/Import', data=body, method='POST', headers={
        'Content-Type': 'application/json',
        'Authorization': 'MediaBrowser Token="' + urllib.parse.quote(key, safe='') + '"',
    })
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.load(response)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--db', type=Path, default=Path(os.environ.get('PLAYBACK_REPORTING_DB', DEFAULT_DB)), help='playback_reporting.db (default: %(default)s)')
    parser.add_argument('--url', default=os.environ.get('JELLYFIN_LOCAL', 'http://127.0.0.1:8096'), help='Jellyfin address (default: %(default)s)')
    parser.add_argument('--time-zone', default=local_zone(), help='IANA zone of the stored times (default: this machine, %(default)s)')
    parser.add_argument('--dry-run', action='store_true', help='count rows without sending them')
    args = parser.parse_args()

    if not args.db.exists():
        sys.exit(f'No Playback Reporting database at {args.db}. Pass --db.')
    rows = read_rows(args.db)
    print(f'{len(rows)} plays in {args.db.name}' + (f', {min(r["DateCreated"] for r in rows)} to {max(r["DateCreated"] for r in rows)}' if rows else ''))
    if args.dry_run or not rows:
        return
    key = os.environ.get('JELLYFIN_API_KEY')
    if not key:
        sys.exit('Set JELLYFIN_API_KEY to an API key from Dashboard > API Keys.')

    totals = {'added': 0, 'skipped': 0, 'overlap': 0, 'invalid': 0}
    for start in range(0, len(rows), BATCH):
        try:
            result = send(args.url, key, args.time_zone, rows[start:start + BATCH])
        except urllib.error.HTTPError as error:
            sys.exit(f'Import stopped at row {start}: HTTP {error.code}. Is the Zorofin plugin installed and up to date?')
        for k in totals:
            totals[k] += result.get(k, 0)
        print(f'  {min(start + BATCH, len(rows))}/{len(rows)}')
    print(f"Added {totals['added']}, already there {totals['skipped']}, unusable {totals['invalid']}.")
    if totals['overlap']:
        print(f"Left out {totals['overlap']} plays from after the plugin started recording, which it already has.")


if __name__ == '__main__':
    main()
