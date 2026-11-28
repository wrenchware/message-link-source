# Copyright (c) 2026 Wrenchware. MIT License; see LICENSE.
"""Client for a Message Link server's report drop box.

Standard library only, Python 3.10 or later. Shared by the ml-report script and the Hermes delivery plugin.

A connection code is ``mlrc1.`` followed by base64url (no padding) of a JSON object::

    {"v": 1, "url": "https://<host>[:port]/v1/reports", "sha256": "<64 lowercase hex>", "token": "mlr1_<43 base64url>"}

``sha256`` is the SHA-256 of the server's DER certificate. The certificate is self-signed, so the client pins it instead
of checking a name: the TLS handshake completes, the pin is compared, and only then is any request byte sent.

Neither the code nor the token ever appears in an error message; a message may name the host.
"""

import base64
import binascii
import hashlib
import hmac
import http.client
import json
import re
import socket
import ssl
import urllib.parse

__all__ = [
    "ReportError", "SetupError", "CodeError", "PinError", "UnreachableError", "RefusedError", "UncertainError",
    "Result", "read_code", "send", "check", "exit_code", "MAX_TEXT", "MAX_BODY", "TIMEOUT",
]

PREFIX = "mlrc1."
PATH = "/v1/reports"
CHECK_PATH = "/v1/reports/check"
MAX_TEXT = 8000          # characters, after trimming
MAX_BODY = 16 * 1024     # bytes on the wire
TIMEOUT = 10.0           # seconds, for connecting and for each read

_BASE64URL = re.compile(r"[A-Za-z0-9_-]+")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_TOKEN = re.compile(r"mlr1_[A-Za-z0-9_-]{43}")
_KEY = re.compile(r"[A-Za-z0-9._-]{1,64}")

UNKNOWN_CODE = ("the server doesn't know this code (turned off, replaced, "
                "or not from the home network)")


class ReportError(Exception):
    """A report or check that did not succeed. ``exit_code`` is what a command line tool should exit with."""
    exit_code = 1


class SetupError(ReportError):
    """Something to fix in the setup: the code, the pin or the network."""
    exit_code = 2


class CodeError(SetupError):
    """The connection code is missing or malformed. Nothing was sent."""


class PinError(SetupError):
    """The server's certificate is not the one the code names. Nothing was sent."""


class UnreachableError(SetupError):
    """The server could not be reached. Nothing was sent."""


class RefusedError(ReportError):
    """The server answered, but not with success; or the text was refused before connecting."""


class UncertainError(RefusedError):
    """The request was sent but no usable answer came back: the report may have arrived. Never send it again."""


class Result:
    """A successful answer: ``status`` is "delivered", "duplicate" or "ok" (a check); ``id`` the message id if given."""

    def __init__(self, status, id=None):
        self.status = status
        self.id = id

    def __repr__(self):
        return "Result(%r, %r)" % (self.status, self.id)


def exit_code(outcome):
    """0 for a Result (delivered, duplicate, check passed) or None; 1 for a refusal or failure; 2 for a setup problem."""
    if outcome is None or isinstance(outcome, Result):
        return 0
    if isinstance(outcome, ReportError):
        return outcome.exit_code
    return 1


def read_code(code):
    """Validate a connection code. Returns ``(url_parts, sha256_hex, token)``; url_parts is a urllib SplitResult.

    Raises CodeError, with a plain message naming the rule, for anything else.
    """
    if not isinstance(code, str) or not code.strip():
        raise CodeError("there is no connection code")
    code = code.strip()
    if not code.startswith(PREFIX):
        raise CodeError("this isn't a Message Link connection code (wrong prefix)")
    payload = code[len(PREFIX):]
    if "=" in payload:
        raise CodeError("the connection code has padding (=), which it never has")
    if not _BASE64URL.fullmatch(payload) or len(payload) % 4 == 1:
        raise CodeError("the connection code isn't valid base64url")
    try:
        raw = base64.b64decode(payload + "=" * (-len(payload) % 4), altchars=b"-_", validate=True)
        data = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique, parse_constant=_no_constant)
    except (binascii.Error, ValueError):
        raise CodeError("the connection code isn't readable") from None
    if not isinstance(data, dict):
        raise CodeError("the connection code isn't a JSON object")

    version = data.get("v")
    if type(version) is not int or version != 1:
        raise CodeError("the connection code is for another version (v must be 1)")

    url = data.get("url")
    if not isinstance(url, str):
        raise CodeError("the connection code has no address")
    parts = _check_url(url)

    pin = data.get("sha256")
    if not isinstance(pin, str) or not _SHA256.fullmatch(pin):
        raise CodeError("the connection code's certificate fingerprint isn't 64 lowercase hex characters")

    token = data.get("token")
    if not isinstance(token, str) or not _TOKEN.fullmatch(token):
        raise CodeError("the connection code's token isn't in the expected form")
    return parts, pin, token


def _unique(pairs):
    seen = {}
    for key, value in pairs:
        if key in seen:
            raise ValueError("duplicate field")
        seen[key] = value
    return seen


def _no_constant(name):
    raise ValueError("not JSON")


def _check_url(url):
    if any(ord(c) <= 0x20 or ord(c) == 0x7F for c in url) or not url.isascii():
        raise CodeError("the connection code's address has spaces or other characters an address can't have")
    if "?" in url:
        raise CodeError("the connection code's address has a query, which it never has")
    if "#" in url:
        raise CodeError("the connection code's address has a fragment, which it never has")
    try:
        parts = urllib.parse.urlsplit(url)
    except ValueError:
        raise CodeError("the connection code's address isn't a valid address") from None
    if parts.scheme != "https" or not url.startswith("https://"):
        raise CodeError("the connection code's address isn't https")
    if "@" in parts.netloc:
        raise CodeError("the connection code's address has a user name in it, which it never has")
    try:
        host, port = parts.hostname, parts.port
    except ValueError:
        raise CodeError("the connection code's address has an invalid port") from None
    if not host:
        raise CodeError("the connection code's address has no host")
    if port == 0 or parts.netloc.endswith(":"):
        raise CodeError("the connection code's address has an invalid port")
    if parts.path != PATH:
        raise CodeError("the connection code's address doesn't end in /v1/reports")
    return parts


def _validate_text(text):
    if not isinstance(text, str):
        raise RefusedError("the report has no text")
    trimmed = text.strip()
    if not trimmed:
        raise RefusedError("the report is empty")
    # Counted as the server counts: UTF-16 units, so a character outside the Basic Multilingual Plane counts twice.
    if len(trimmed) + sum(1 for c in trimmed if ord(c) > 0xFFFF) > MAX_TEXT:
        raise RefusedError("the report is longer than %d characters" % MAX_TEXT)
    try:
        body = text.encode("utf-8")
    except UnicodeEncodeError:
        raise RefusedError("the report isn't valid UTF-8 text") from None
    if len(body) > MAX_BODY:
        raise RefusedError("the report is larger than %d KiB" % (MAX_BODY // 1024))
    return body


class _PinnedConnection(http.client.HTTPSConnection):
    """HTTPS that trusts exactly one certificate, compared before any request byte is written."""

    def __init__(self, host, port, pin, timeout):
        context = ssl.create_default_context()
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        super().__init__(host, port, timeout=timeout, context=context)
        self._pin_context = context
        self._pin = pin

    def connect(self):
        where = _where(self.host, self.port)
        try:
            raw = socket.create_connection((self.host, self.port), self.timeout, self.source_address)
        except OSError as error:
            raise UnreachableError("can't reach the server at %s (%s)" % (where, _reason(error))) from None
        try:
            tls = self._pin_context.wrap_socket(raw, server_hostname=self.host)
        except (OSError, ssl.SSLError) as error:
            raw.close()
            raise UnreachableError("can't open a secure connection to %s (%s)" % (where, _reason(error))) from None
        der = tls.getpeercert(binary_form=True)
        if not der or not hmac.compare_digest(hashlib.sha256(der).hexdigest(), self._pin):
            tls.close()
            raise PinError("the server at %s isn't the one this code was made for (its certificate doesn't match)" % where)
        self.sock = tls


def _where(host, port):
    return ("[%s]:%d" if ":" in host else "%s:%d") % (host, port)


def _reason(error):
    if isinstance(error, socket.timeout):
        return "timed out"
    if isinstance(error, ConnectionRefusedError):
        return "connection refused"
    if isinstance(error, socket.gaierror):
        return "unknown host"
    if isinstance(error, ssl.SSLError):
        return "TLS failed"
    return error.strerror or type(error).__name__


def _exchange(parts, pin, token, method, path, body=None, headers=None, timeout=TIMEOUT):
    """One request on a fresh pinned connection. Returns (status, headers, body bytes). Never follows redirects."""
    connection = _PinnedConnection(parts.hostname, parts.port or 443, pin, timeout)
    all_headers = {"Host": parts.netloc, "Authorization": "Bearer " + token, "Connection": "close"}
    all_headers.update(headers or {})
    try:
        connection.connect()
        try:
            connection.request(method, path, body=body, headers=all_headers)
            response = connection.getresponse()
            answer = response.read(64 * 1024)
            return response.status, response, answer
        except (OSError, http.client.HTTPException) as error:
            # The request may have reached the server: say so, and never retry it.
            raise UncertainError("no answer from %s (%s); the report may or may not have arrived"
                               % (_where(parts.hostname, parts.port or 443), _reason(error) if isinstance(error, OSError)
                                  else "the answer was unreadable")) from None
    finally:
        connection.close()


def _error_word(answer):
    try:
        data = json.loads(answer.decode("utf-8"))
    except (UnicodeDecodeError, ValueError):
        return None
    return data.get("error") if isinstance(data, dict) and isinstance(data.get("error"), str) else None


def _failure(status, response, answer, report=True):
    if 300 <= status < 400:
        return RefusedError("the server answered with a redirect (%d), which is never followed" % status)
    if status == 404:
        return RefusedError(UNKNOWN_CODE)
    if status == 409:
        return RefusedError("this key was already used for a different report (key_reused)")
    if status == 400:
        word = _error_word(answer)
        reason = {"empty": "the report is empty", "invalid_text": "the report's text isn't valid",
                  "invalid_key": "the key isn't valid"}.get(word, "the server couldn't use the report")
        return RefusedError("%s (%s)" % (reason, word or "bad request"))
    if status == 413:
        return RefusedError("the report is too large")
    if status == 415:
        return RefusedError("the server refused the report's content type")
    if status == 429:
        after = (response.getheader("Retry-After") or "").strip()
        wait = ("; try again in %s seconds" % after) if after.isdigit() else ""
        return RefusedError("too many reports for now (rate_limited)%s" % wait)
    if status == 503:
        return RefusedError("the server isn't ready (starting, stopping, or Hermes isn't loaded)")
    if not report:
        return RefusedError("the server answered %d" % status)
    # Every answer above says nothing was stored; an unexpected one doesn't say, so it counts as uncertain.
    return UncertainError("the server answered %d; the report may or may not have arrived" % status)


def send(code, text, key=None, timeout=TIMEOUT):
    """Post one report. Returns Result("delivered" or "duplicate", id); raises ReportError otherwise."""
    parts, pin, token = read_code(code)
    if key is not None and (not isinstance(key, str) or not _KEY.fullmatch(key)):
        raise CodeError("the key must be 1 to 64 of A-Z, a-z, 0-9, dot, underscore or hyphen")
    body = _validate_text(text)
    headers = {"Content-Type": "text/plain; charset=utf-8"}
    if key is not None:
        headers["Idempotency-Key"] = key
    status, response, answer = _exchange(parts, pin, token, "POST", PATH, body, headers, timeout)
    if status in (200, 202):
        try:
            data = json.loads(answer.decode("utf-8"))
        except (UnicodeDecodeError, ValueError):
            data = None
        message_id = data.get("id") if isinstance(data, dict) and isinstance(data.get("id"), str) else None
        return Result("delivered" if status == 202 else "duplicate", message_id)
    raise _failure(status, response, answer)


def check(code, timeout=TIMEOUT):
    """Test a code against its server. Returns Result("ok"); raises ReportError otherwise."""
    parts, pin, token = read_code(code)
    status, response, answer = _exchange(parts, pin, token, "GET", CHECK_PATH, None, None, timeout)
    if status == 204:
        return Result("ok")
    raise _failure(status, response, answer, report=False)
