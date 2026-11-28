# Copyright (c) 2026 Wrenchware. MIT License; see LICENSE.
"""Self-test for the report client, standard library only. The server image's build runs it, so an image never ships
Python files that don't work.

    python3 integrations/hermes-messagelink/tests/selftest.py

It checks that every Python file in the folder compiles, that connection codes are read and refused by their rules,
and, with the openssl command to make a throwaway certificate, that a certificate other than the pinned one is refused
with no request byte sent, the pinned one delivers, and a lost answer is reported as uncertain. Without openssl those
are skipped locally, but fail the run in GitHub Actions (GITHUB_ACTIONS=true) or with MESSAGELINK_SELFTEST_REQUIRE_TLS=1,
so the image's build can't pass without them. It is not part of either download.
"""

import base64
import hashlib
import json
import os
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
import unittest

FOLDER = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, FOLDER)

import mlreport_client as client  # noqa: E402

# Made up for the test: never a real server's token.
TOKEN = "mlr1_" + "A" * 43


def tls_required():
    """True where the TLS checks must run: GitHub Actions, or when asked for."""
    return os.environ.get("GITHUB_ACTIONS") == "true" or os.environ.get("MESSAGELINK_SELFTEST_REQUIRE_TLS") == "1"


def make_code(url, pin, token=TOKEN, version=1):
    data = json.dumps({"v": version, "url": url, "sha256": pin, "token": token}).encode("utf-8")
    return "mlrc1." + base64.urlsafe_b64encode(data).decode("ascii").rstrip("=")


class Compiles(unittest.TestCase):
    def test_python_version(self):
        self.assertGreaterEqual(sys.version_info[:2], (3, 10))

    def test_every_file_compiles(self):
        names = [name for name in os.listdir(FOLDER) if name.endswith(".py") or name == "ml-report"]
        self.assertTrue({"ml-report", "mlreport_client.py", "adapter.py", "__init__.py"} <= set(names))
        for name in names:
            with open(os.path.join(FOLDER, name), encoding="utf-8") as file:
                compile(file.read(), name, "exec")


class Codes(unittest.TestCase):
    PIN = "0" * 64
    URL = "https://192.168.1.20:8787/v1/reports"

    def test_a_good_code(self):
        parts, pin, token = client.read_code(make_code(self.URL, self.PIN))
        self.assertEqual((parts.hostname, parts.port, pin, token), ("192.168.1.20", 8787, self.PIN, TOKEN))

    def test_malformed_codes(self):
        cases = {
            "version": make_code(self.URL, self.PIN, version=2),
            "http": make_code("http://192.168.1.20:8787/v1/reports", self.PIN),
            "path": make_code("https://192.168.1.20:8787/v1/reports/x", self.PIN),
            "userinfo": make_code("https://user@192.168.1.20:8787/v1/reports", self.PIN),
            "query": make_code(self.URL + "?a=1", self.PIN),
            "fragment": make_code(self.URL + "#x", self.PIN),
            "short pin": make_code(self.URL, self.PIN[:63]),
            "uppercase pin": make_code(self.URL, "A" * 64),
            "short token": make_code(self.URL, self.PIN, TOKEN[:-1]),
            "padding": make_code(self.URL, self.PIN) + "=",
            "prefix": make_code(self.URL, self.PIN).replace("mlrc1.", "mlrc2."),
            "empty": "",
        }
        for name, code in cases.items():
            with self.subTest(name):
                with self.assertRaises(client.CodeError):
                    client.read_code(code)

    def test_text_limits(self):
        code = make_code(self.URL, self.PIN)
        with self.assertRaises(client.RefusedError):
            client.send(code, "x" * (client.MAX_TEXT + 1), timeout=1)
        # Counted as the server counts, in UTF-16 units.
        with self.assertRaises(client.RefusedError):
            client.send(code, "\U0001F600" * (client.MAX_TEXT // 2 + 1), timeout=1)


class _Server:
    """A TLS server on 127.0.0.1 that answers every request 202 (or, with answer=False, closes without answering),
    counting what clients write after the handshake."""

    def __init__(self, certificate, key, answer=True):
        self.answer = answer
        self.context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        self.context.load_cert_chain(certificate, key)
        self.listener = socket.create_server(("127.0.0.1", 0))
        self.port = self.listener.getsockname()[1]
        self.received = 0
        self.served = threading.Event()
        self.thread = threading.Thread(target=self._serve, daemon=True)
        self.thread.start()

    def _serve(self):
        while True:
            try:
                raw, _ = self.listener.accept()
            except OSError:
                return
            raw.settimeout(3)
            try:
                with self.context.wrap_socket(raw, server_side=True) as tls:
                    data = b""
                    # The whole request, head and body, before answering: closing on unread bytes resets the connection.
                    while b"\r\n\r\n" not in data or len(data) < self._length(data):
                        chunk = tls.recv(4096)
                        if not chunk:
                            break
                        data += chunk
                        self.received += len(chunk)
                    if data and self.answer:
                        body = b'{"id":"m-1","status":"delivered"}'
                        tls.sendall(b"HTTP/1.1 202 Accepted\r\nContent-Type: application/json\r\nContent-Length: "
                                    + str(len(body)).encode() + b"\r\nConnection: close\r\n\r\n" + body)
            except (OSError, ssl.SSLError):
                pass
            finally:
                self.served.set()

    @staticmethod
    def _length(data):
        """The full request's length in bytes, once its head is in."""
        head, _, _ = data.partition(b"\r\n\r\n")
        for line in head.split(b"\r\n")[1:]:
            name, _, value = line.partition(b":")
            if name.strip().lower() == b"content-length":
                return len(head) + 4 + int(value.strip())
        return len(head) + 4

    def close(self):
        self.listener.close()


class Pinning(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if shutil.which("openssl") is None:
            if tls_required():
                raise RuntimeError("the openssl command is required here to make a throwaway certificate, and none was found")
            raise unittest.SkipTest("no openssl command to make a throwaway certificate")
        cls.folder = tempfile.mkdtemp()
        cls.certificate = os.path.join(cls.folder, "cert.pem")
        cls.key = os.path.join(cls.folder, "key.pem")
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", cls.key,
                        "-out", cls.certificate, "-days", "2", "-subj", "/CN=selftest"],
                       check=True, capture_output=True, timeout=60)
        with open(cls.certificate, encoding="ascii") as file:
            der = ssl.PEM_cert_to_DER_cert(file.read())
        cls.pin = hashlib.sha256(der).hexdigest()

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.folder, ignore_errors=True)

    def setUp(self):
        self.server = _Server(self.certificate, self.key)
        self.url = "https://127.0.0.1:%d/v1/reports" % self.server.port

    def tearDown(self):
        self.server.close()

    def test_another_certificate_gets_no_bytes(self):
        with self.assertRaises(client.PinError):
            client.send(make_code(self.url, "1" * 64), "secret report", timeout=3)
        self.assertTrue(self.server.served.wait(3))
        self.assertEqual(self.server.received, 0)

    def test_the_pinned_certificate_delivers(self):
        result = client.send(make_code(self.url, self.pin), "report", timeout=3)
        self.assertEqual((result.status, result.id), ("delivered", "m-1"))

    def test_a_lost_answer_is_uncertain(self):
        self.server.close()
        self.server = _Server(self.certificate, self.key, answer=False)
        url = "https://127.0.0.1:%d/v1/reports" % self.server.port
        with self.assertRaises(client.UncertainError) as caught:
            client.send(make_code(url, self.pin), "report", timeout=3)
        self.assertIn("may or may not have arrived", str(caught.exception))
        self.assertGreater(self.server.received, 0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
