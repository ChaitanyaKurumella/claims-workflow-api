#!/usr/bin/env python3
"""Consistent SQLite online backup. Never overwrite an existing backup."""
import argparse
import sqlite3
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('source',type=Path);p.add_argument('destination',type=Path)
a=p.parse_args()
source=a.source.resolve();destination=a.destination.resolve()
if not source.is_file():p.error('source does not exist')
if destination.exists():p.error('destination already exists')
destination.parent.mkdir(parents=True,exist_ok=True)
# Exclusive creation prevents accidental replacement; unlink incomplete backups on failure.
with destination.open('xb'):pass
try:
    with sqlite3.connect(source.as_uri()+'?mode=ro',uri=True) as src,sqlite3.connect(destination) as dst:
        src.backup(dst)
        if dst.execute('PRAGMA integrity_check').fetchone()[0]!='ok':raise RuntimeError('integrity check failed')
        if dst.execute('PRAGMA foreign_key_check').fetchall():raise RuntimeError('foreign key check failed')
    print(f'Verified backup: {destination}')
except BaseException:
    destination.unlink(missing_ok=True)
    raise
