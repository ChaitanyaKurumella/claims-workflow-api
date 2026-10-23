#!/usr/bin/env python3
"""Loopback HTTP receiver with durable event-key deduplication, for delivery exercises."""
import argparse,json,sqlite3
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--port',type=int,default=5092);p.add_argument('--database',default='receiver.db');p.add_argument('--fail-first',type=int,default=0);a=p.parse_args()
with sqlite3.connect(a.database) as db:db.execute('CREATE TABLE IF NOT EXISTS receipts(event_key TEXT PRIMARY KEY,payload TEXT NOT NULL,received_at TEXT DEFAULT CURRENT_TIMESTAMP)')
class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path!='/events':self.send_error(404);return
        size=int(self.headers.get('Content-Length','0'))
        if size<=0 or size>1_000_000:self.send_error(413);return
        key=self.headers.get('Idempotency-Key');payload=self.rfile.read(size).decode()
        if not key:self.send_error(400);return
        try:json.loads(payload)
        except ValueError:self.send_error(400);return
        with sqlite3.connect(a.database) as db:
            db.execute('BEGIN IMMEDIATE')
            existing=db.execute('SELECT payload FROM receipts WHERE event_key=?',(key,)).fetchone()
            if existing and existing[0]!=payload:self.send_error(409);return
            if not existing:db.execute('INSERT INTO receipts(event_key,payload) VALUES(?,?)',(key,payload))
        # Intentionally fail AFTER durable acceptance to exercise the acknowledgement gap.
        if a.fail_first>0:a.fail_first-=1;self.send_error(503);return
        self.send_response(200);self.send_header('Content-Type','application/json');self.end_headers();self.wfile.write(b'{"accepted":true}')
ThreadingHTTPServer(('127.0.0.1',a.port),Handler).serve_forever()
