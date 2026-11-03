"""Standalone CubeWorld server directory, with the original Unity wire format."""

import argparse
import ipaddress
import json
import re
import sqlite3
import time
from contextlib import closing
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit


LEASE_SECONDS = 60
LIST_LIMIT = 20


class Directory:
    def __init__(self, database, clock=time.time):
        self.database = database
        self.clock = clock
        with closing(sqlite3.connect(database)) as connection, connection:
            connection.execute("""
                CREATE TABLE IF NOT EXISTS servers (
                    ip TEXT NOT NULL,
                    port INTEGER NOT NULL,
                    owner TEXT NOT NULL,
                    description TEXT NOT NULL,
                    renewed REAL NOT NULL,
                    PRIMARY KEY (ip, port)
                )
            """)
            connection.execute("CREATE INDEX IF NOT EXISTS servers_renewed ON servers (renewed)")

    def register(self, ip, port, owner, description):
        with closing(sqlite3.connect(self.database)) as connection, connection:
            connection.execute("BEGIN IMMEDIATE")
            now = self.clock()
            connection.execute("DELETE FROM servers WHERE renewed <= ?", (now - LEASE_SECONDS,))
            connection.execute("""
                INSERT INTO servers (ip, port, owner, description, renewed)
                VALUES (?, ?, ?, ?, ?)
                ON CONFLICT(ip, port) DO UPDATE SET
                    owner = excluded.owner,
                    description = excluded.description,
                    renewed = excluded.renewed
            """, (ip, port, owner, description, now))

    def list(self):
        with closing(sqlite3.connect(self.database)) as connection:
            connection.row_factory = sqlite3.Row
            rows = connection.execute("""
                SELECT ip, port, owner, description FROM servers
                WHERE renewed > ?
                ORDER BY renewed DESC, ip, port
                LIMIT ?
            """, (self.clock() - LEASE_SECONDS, LIST_LIMIT))
            return [dict(row) for row in rows]


def registration(query):
    fields = parse_qs(query, keep_blank_values=True, strict_parsing=True,
                      encoding="utf-8", errors="strict", max_num_fields=3)
    if fields.keys() != {"port", "owner", "description"} or any(
        len(values) != 1 for values in fields.values()
    ):
        raise ValueError("Expected one port, owner and description")
    port = fields["port"][0]
    if not re.fullmatch(r"[0-9]{1,5}", port) or not 1 <= int(port) <= 65535:
        raise ValueError("Invalid TCP port")

    owner = fields["owner"][0].strip()
    description = fields["description"][0].strip()
    for label in (owner, description):
        if not 2 <= len(label) <= 80 or not label.isprintable() or "," in label or ";" in label:
            raise ValueError("Invalid server label")
    return int(port), owner, description


class DiscoveryHandler(BaseHTTPRequestHandler):
    def respond(self, status, body, media_type="text/plain"):
        payload = body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", media_type + "; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        try:
            target = urlsplit(self.path)
        except ValueError:
            self.respond(400, "ERROR")
            return

        if target.path == "/register":
            try:
                port, owner, description = registration(target.query)
            except ValueError:
                self.respond(400, "ERROR")
                return

            ip = str(ipaddress.ip_address(self.client_address[0]))
            try:
                self.server.directory.register(ip, port, owner, description)
            except sqlite3.Error:
                self.log_error("Could not write to the server directory")
                self.respond(503, "ERROR")
                return
            self.respond(200, "OK")
        elif target.path in ("/list", "/servers", "/health"):
            try:
                servers = self.server.directory.list()
            except sqlite3.Error:
                self.log_error("Could not read from the server directory")
                self.respond(503, "ERROR")
                return
            if target.path == "/list":
                wire = "".join(
                    "{ip},{port},{owner},{description};".format(**server) for server in servers
                )
                self.respond(200, wire)
            elif target.path == "/servers":
                self.respond(200, json.dumps(servers), "application/json")
            else:
                self.respond(200, "OK")
        else:
            self.respond(404, "Not found")

    def log_request(self, code="-", size="-"):
        # Avoid recording host labels supplied in registration query strings.
        self.log_message("%s %s %s", self.command, self.path.split("?", 1)[0], code)


class DiscoveryServer(ThreadingHTTPServer):
    def __init__(self, address, directory):
        self.directory = directory
        super().__init__(address, DiscoveryHandler)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind", default="127.0.0.1", help="IPv4 interface (default: loopback)")
    parser.add_argument("--port", type=int, default=8081, help="HTTP port (default: 8081)")
    parser.add_argument("--database", type=Path, default=Path("cubeworld.sqlite3"))
    args = parser.parse_args()
    if not 0 <= args.port <= 65535:
        parser.error("port must be between 0 and 65535")
    with DiscoveryServer((args.bind, args.port), Directory(args.database)) as server:
        print("CubeWorld discovery listening at http://{}:{}".format(*server.server_address), flush=True)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
