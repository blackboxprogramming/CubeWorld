import concurrent.futures
import json
import os
import signal
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from cubeworld import DiscoveryServer, Directory


HEADLESS_DLL = os.environ.get("CUBEWORLD_HEADLESS_DLL")


class DiscoveryTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.database = Path(temp.name) / "directory.sqlite3"
        self.now = time.time()
        self.start_server()

    def start_server(self):
        server = DiscoveryServer(("127.0.0.1", 0), Directory(self.database, lambda: self.now))
        self.url = "http://127.0.0.1:" + str(server.server_port)
        thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.01})
        thread.start()

        def stop_server():
            if thread.is_alive():
                server.shutdown()
                thread.join()
            server.server_close()

        self.stop_server = stop_server
        self.addCleanup(stop_server)

    def get(self, route, headers=None):
        try:
            response = urlopen(Request(self.url + route, headers=headers or {}), timeout=5)
        except HTTPError as error:
            response = error
        with response:
            return response.status, response.read().decode("utf-8")

    def register(self, port=9999, owner="Road & friends", description="Café + caves", headers=None):
        query = urlencode({"port": port, "owner": owner, "description": description})
        return self.get("/register?" + query, headers)

    def test_empty_health_and_missing_route(self):
        self.assertEqual(self.get("/list"), (200, ""))
        self.assertEqual(self.get("/servers"), (200, "[]"))
        self.assertEqual(self.get("/health"), (200, "OK"))
        self.assertEqual(self.get("/missing"), (404, "Not found"))

    def test_legacy_wire_json_and_peer_address(self):
        self.assertEqual(self.register(headers={"X-Forwarded-For": "198.51.100.5"}), (200, "OK"))
        self.assertEqual(self.get("/list"), (200, "127.0.0.1,9999,Road & friends,Café + caves;"))
        status, body = self.get("/servers")
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(body), [{
            "ip": "127.0.0.1", "port": 9999, "owner": "Road & friends", "description": "Café + caves",
        }])

    def test_invalid_registration_keeps_active_lease(self):
        self.assertEqual(self.register(), (200, "OK"))
        for query in (
            "port=0&owner=Road&description=Caves",
            "port=65536&owner=Road&description=Caves",
            "port=x&owner=Road&description=Caves",
            "port=9999&owner=Road&owner=Other",
            "port=9999&owner=Road",
            "port=9999&owner=Road&description=bad%3Blabel",
            "port=9999&owner=Road&description=bad%2Clabel",
            "port=9999&owner=Road&description=bad%0Alabel",
            "port=9999&owner=Road&description=%FF",
        ):
            with self.subTest(query=query):
                self.assertEqual(self.get("/register?" + query), (400, "ERROR"))
        self.assertEqual(self.get("/list"), (200, "127.0.0.1,9999,Road & friends,Café + caves;"))

    def test_renewal_expiration_and_distinct_ports(self):
        self.assertEqual(self.register(), (200, "OK"))
        self.assertEqual(self.register(port=65535), (200, "OK"))
        self.now += 50
        self.assertEqual(self.register(description="Updated world"), (200, "OK"))
        self.now += 10
        self.assertEqual(self.get("/list"), (200, "127.0.0.1,9999,Road & friends,Updated world;"))
        self.now += 50
        self.assertEqual(self.get("/list"), (200, ""))

    def test_restart_preserves_lease(self):
        self.assertEqual(self.register(), (200, "OK"))
        self.stop_server()
        self.start_server()
        self.assertEqual(self.get("/list"), (200, "127.0.0.1,9999,Road & friends,Café + caves;"))

    def test_concurrent_writes_and_newest_twenty(self):
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
            responses = list(pool.map(self.register, range(10000, 10024)))
        self.assertEqual(responses, [(200, "OK")] * 24)
        self.now += 1
        self.assertEqual(self.register(port=11000), (200, "OK"))
        entries = json.loads(self.get("/servers")[1])
        self.assertEqual([entry["port"] for entry in entries], [11000, *range(10000, 10019)])

    @unittest.skipUnless(HEADLESS_DLL, "set CUBEWORLD_HEADLESS_DLL to run engine integration")
    def test_original_engine_http_and_tcp_workflow(self):
        result = subprocess.run(["dotnet", HEADLESS_DLL, "smoke", self.url],
                                capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("PASS XML world generation", result.stdout)
        self.assertIn("PASS HTTP registration and discovery", result.stdout)
        self.assertIn("PASS TCP join", result.stdout)

    @unittest.skipUnless(HEADLESS_DLL and os.name == "posix", "requires the headless build and POSIX signals")
    def test_standalone_host_advertises_and_serves_initial_snapshot(self):
        environment = dict(os.environ, CUBEWORLD_DISCOVERY_URL=self.url)
        with subprocess.Popen(["dotnet", HEADLESS_DLL, "serve", "0"], env=environment,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True) as host:
            try:
                deadline = time.monotonic() + 10
                listing = []
                while not listing and time.monotonic() < deadline and host.poll() is None:
                    listing = json.loads(self.get("/servers")[1])
                    if not listing:
                        time.sleep(0.05)
                self.assertEqual(len(listing), 1, "headless host never advertised")
                server = listing[0]
                with socket.create_connection((server["ip"], server["port"]), timeout=5) as client:
                    client.sendall(b"\x01")
                    with client.makefile("rb") as stream:
                        (frame_size,) = struct.unpack("<I", stream.read(4))
                        self.assertTrue(5 < frame_size <= 1024 * 1024)
                        self.assertEqual(stream.read(1), b"\x00")  # INITIAL_DATA
            finally:
                if host.poll() is None:
                    host.send_signal(signal.SIGINT)
                try:
                    output, _ = host.communicate(timeout=5)
                except subprocess.TimeoutExpired:
                    host.kill()
                    host.communicate()
                    raise
            self.assertEqual(host.returncode, 0, output)
