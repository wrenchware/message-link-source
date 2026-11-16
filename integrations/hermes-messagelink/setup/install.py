#!/usr/bin/env python3
# Copyright (c) 2026 Wrenchware. MIT License; see LICENSE.
"""Sets up Message Link reports for one Hermes profile, where Hermes runs.

Run by the command the Message Link server's page shows (through install.sh on Linux and macOS, or directly on
Windows), with the Python Hermes itself uses:

    MLSETUP=<setup code> python3 -I install.py [--hermes <path>] [--no-shared-network] <profile> <sha256> <address>...

``<sha256>`` is the SHA-256 of the server's certificate, which every connection pins before it sends a byte, and each
``<address>`` (host:port) is one the server may be reached at. The setup code is read from the environment only, so it
never shows in a process list, and works once.

What it does, in order, stopping at the first problem:

1. Checks the profile's folder and its ``config.yaml`` can be changed safely. Nothing has changed yet.
2. Tries each address (the one the profile's current code names first) until the server answers from one.
3. Makes the profile's report token here and sends the server only its hash, with the address that worked. The token
   never travels; the setup code is then used up.
4. Puts ml-report, its skill and the messagelink plugin in the profile, sets the code in its ``.env`` (as
   ``MESSAGELINK_CODE`` for the plugin and ``MESSAGE_LINK_REPORTS`` for commands), enables the plugin, lets commands
   see the code and turns Hermes's restart notices off for Message Link in
   ``config.yaml`` (keeping its comments), then checks the new code once.
5. Restarts Hermes's gateway, which commands need before they see the code, unless a chat or job is running in it.

Running it again with a new setup code replaces the code and changes nothing else. Standard library only.
"""

import base64
import hashlib
import hmac
import http.client
import io
import json
import os
import re
import secrets
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.parse
import zipfile

# The server puts the files in here, as a base64 zip, when it serves this installer; the command checks the whole.
BUNDLE = None

SETUP_ENV = "MLSETUP"
# The same code under two names. The plugin's manifest declares CODE_ENV as a password, so Hermes counts it as a
# platform secret and never passes it to commands (local_env_policy._home_adapter_secret_env, v0.23.1). Commands, and
# so ml-report, get COMMAND_ENV through terminal.env_passthrough instead.
CODE_ENV = "MESSAGELINK_CODE"
COMMAND_ENV = "MESSAGE_LINK_REPORTS"
PROBE_PATH = "/v1/reports/setup/probe"
REDEEM_PATH = "/v1/reports/setup/redeem"
CHECK_PATH = "/v1/reports/check"
REPORT_PATH = "/v1/reports"
PENDING = ".messagelink-setup"
TIMEOUT = 10.0

_SETUP = re.compile(r"mls1_[A-Za-z0-9_-]{43}")
_SHA256 = re.compile(r"[0-9a-f]{64}")
_PROFILE = re.compile(r"[a-z0-9][a-z0-9_-]{0,63}")
_NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")

# Where each file of the bundle goes in the profile's folder, and whether it is run directly.
PLACES = {
    "ml-report": ["scripts/ml-report"],
    "mlreport_client.py": ["scripts/mlreport_client.py", "plugins/messagelink/mlreport_client.py"],
    "SKILL.md": ["skills/message-link-reports/SKILL.md"],
    "plugin.yaml": ["plugins/messagelink/plugin.yaml"],
    "__init__.py": ["plugins/messagelink/__init__.py"],
    "adapter.py": ["plugins/messagelink/adapter.py"],
    "README.md": ["plugins/messagelink/README.md"],
    "LICENSE": ["plugins/messagelink/LICENSE"],
}
EXECUTABLE = {"ml-report"}


class Stop(Exception):
    """A problem to tell the person, in their words. ``exit_code`` 2 is something to fix in the setup."""

    def __init__(self, message, exit_code=1):
        super().__init__(message)
        self.exit_code = exit_code


def say(line=""):
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


# ---- Talking to the server ----


class Unreachable(Exception):
    pass


class OtherServer(Exception):
    pass


class NoAnswer(Exception):
    pass


def split_address(address):
    """(host, port) of a host:port the page wrote, or None when it isn't one."""
    if not isinstance(address, str) or not address or not address.isascii() or any(c in address for c in "/?#@ \t\\"):
        return None
    try:
        parts = urllib.parse.urlsplit("https://" + address)
        host, port = parts.hostname, parts.port
    except ValueError:
        return None
    if not host or not port or parts.netloc != address:
        return None
    return host, port


class _Pinned(http.client.HTTPSConnection):
    """HTTPS to exactly one certificate, compared before any request byte is written (as mlreport_client.py does)."""

    def __init__(self, host, port, pin):
        # Trust comes from the pin alone, so no store of authorities is loaded.
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        super().__init__(host, port, timeout=TIMEOUT, context=context)
        self._pin_context = context
        self._pin = pin

    def connect(self):
        try:
            raw = socket.create_connection((self.host, self.port), self.timeout)
        except OSError as error:
            raise Unreachable(_reason(error)) from None
        try:
            tls = self._pin_context.wrap_socket(raw, server_hostname=self.host)
        except (OSError, ssl.SSLError) as error:
            raw.close()
            raise Unreachable(_reason(error)) from None
        der = tls.getpeercert(binary_form=True)
        if not der or not hmac.compare_digest(hashlib.sha256(der).hexdigest(), self._pin):
            tls.close()
            raise OtherServer()
        self.sock = tls


def _reason(error):
    if isinstance(error, socket.timeout):
        return "timed out"
    if isinstance(error, ConnectionRefusedError):
        return "connection refused"
    if isinstance(error, socket.gaierror):
        return "unknown name"
    if isinstance(error, ssl.SSLError):
        return "secure connection failed"
    return error.strerror or type(error).__name__


def exchange(address, pin, secret, method, path, body=None):
    """One request on a fresh pinned connection: (status, headers, body). Never follows a redirect."""
    host, port = split_address(address)
    connection = _Pinned(host, port, pin)
    headers = {"Host": address, "Authorization": "Bearer " + secret, "Connection": "close"}
    if body is not None:
        headers["Content-Type"] = "application/json"
    try:
        connection.connect()
        try:
            connection.request(method, path, body=body, headers=headers)
            response = connection.getresponse()
            return response.status, response, response.read(16 * 1024)
        except (OSError, http.client.HTTPException):
            raise NoAnswer() from None
    finally:
        connection.close()


# ---- The profile ----


def hermes_home():
    """Hermes's home as Hermes resolves it: HERMES_HOME, else ~/.hermes, or hermes in %LOCALAPPDATA% on Windows."""
    home = os.environ.get("HERMES_HOME", "").strip()
    if home:
        return os.path.expanduser(home)
    if os.name == "nt":
        base = os.environ.get("LOCALAPPDATA", "").strip() or os.path.join(os.path.expanduser("~"), "AppData", "Local")
        return os.path.join(base, "hermes")
    return os.path.join(os.path.expanduser("~"), ".hermes")


def profile_folder(profile):
    home = hermes_home()
    return home if profile == "default" else os.path.join(home, "profiles", profile)


def owner_check(folder):
    if not hasattr(os, "geteuid"):
        return
    owner = os.stat(folder).st_uid
    if owner != os.geteuid():
        raise Stop("The profile's folder belongs to another user (id %d). Run this as that user." % owner, 2)


def plain_file(path):
    """A file this setup rewrites must be a plain file or not there yet: rewriting a link would replace it with a file."""
    if os.path.islink(path) or (os.path.lexists(path) and not os.path.isfile(path)):
        raise Stop("%s is a link or not a plain file, so nothing was changed. Make it a plain file, then run the command "
                   "again." % os.path.basename(path), 2)


def read_text(path):
    try:
        with open(path, "rb") as file:
            data = file.read()
    except FileNotFoundError:
        return None
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        raise Stop("%s isn't UTF-8 text, so it wasn't changed." % os.path.basename(path), 2) from None


def write_atomic(path, data, mode):
    """Writes the whole file or nothing: a temporary file beside it (private from the start), flushed to disk, then
    renamed over it."""
    folder = os.path.dirname(path)
    os.makedirs(folder, exist_ok=True)
    handle, temporary = tempfile.mkstemp(prefix="." + os.path.basename(path) + ".", suffix=".tmp", dir=folder)
    try:
        with os.fdopen(handle, "wb") as file:
            file.write(data)
            file.flush()
            os.fsync(file.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    except BaseException:
        try:
            os.unlink(temporary)
        except OSError:
            pass
        raise


def mode_of(path, default):
    try:
        return os.stat(path).st_mode & 0o7777
    except FileNotFoundError:
        return default


def private_mode(path):
    """The mode .env keeps, without anything for other users: it holds the profile's code."""
    return mode_of(path, 0o600) & 0o700 | 0o600


# A dotenv assignment, as python-dotenv (Hermes's reader) takes it: optional export, a name, =, the value.
_ASSIGN = re.compile(r"[ \t]*(?:export[ \t]+)?([A-Za-z_][A-Za-z0-9_.]*)[ \t]*=[ \t]*(.*)$")
OURS = (CODE_ENV, COMMAND_ENV)


def _closes(text, quote, start):
    """Whether a quoted value ends on this line, as python-dotenv reads it ('((?:\\\\.|[^'\\\\])*)' and the same for
    double quotes): in either kind, a backslash takes the next character into the value."""
    index = start
    while index < len(text):
        if text[index] == "\\":
            index += 2
            continue
        if text[index] == quote:
            return True
        index += 1
    return False


def env_lines(text):
    """[(line, name or None, opens)] for .env: the name a line assigns, or None for any other line, including the
    inside of a quoted value that runs over several lines, which is never taken for an assignment."""
    lines = []
    quote = None
    for line in (text or "").splitlines():
        if quote:
            lines.append((line, None, False))
            if _closes(line, quote, 0):
                quote = None
            continue
        match = _ASSIGN.match(line)
        if not match or line.lstrip().startswith("#"):
            lines.append((line, None, False))
            continue
        value = match.group(2)
        opens = value[:1] in ("'", '"') and not _closes(value, value[0], 1)
        if opens:
            quote = value[0]
        lines.append((line, match.group(1), opens))
    if quote:
        raise Stop(".env has a quoted value that isn't closed, so nothing was changed.", 2)
    for _, name, opens in lines:
        if name in OURS and opens:
            raise Stop(".env has a %s value over several lines, so nothing was changed. Remove it, then run the "
                       "command again." % name, 2)
    return lines


def env_code(text):
    """The connection code the profile's .env holds now (the plugin's name first), or None."""
    found = {}
    for line, name, _ in env_lines(text):
        if name in OURS:
            value = _ASSIGN.match(line).group(2).strip()
            if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
                value = value[1:-1]
            found[name] = value
    return found.get(CODE_ENV) or found.get(COMMAND_ENV)


def code_address(code):
    """The host:port a connection code names, or None."""
    if not code or not code.startswith("mlrc1."):
        return None
    payload = code[len("mlrc1."):]
    try:
        data = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)).decode("utf-8"))
        parts = urllib.parse.urlsplit(data["url"])
    except (ValueError, KeyError, TypeError, AttributeError):
        return None
    return parts.netloc if parts.scheme == "https" and parts.path == REPORT_PATH and split_address(parts.netloc) else None


def env_with_code(text, code):
    """.env with this code under both names, every other byte of every other line as it was."""
    newline = "\r\n" if text and "\r\n" in text else "\n"
    lines = [line for line, name, _ in env_lines(text) if name not in OURS]
    while lines and not lines[-1].strip():
        lines.pop()
    lines += ["%s=%s" % (name, code) for name in OURS]
    return newline.join(lines) + newline


def build_code(address, pin, token):
    data = json.dumps({"v": 1, "url": "https://%s%s" % (address, REPORT_PATH), "sha256": pin, "token": token},
                      separators=(",", ":")).encode("utf-8")
    return "mlrc1." + base64.urlsafe_b64encode(data).decode("ascii").rstrip("=")


# ---- config.yaml, edited as text so its comments and layout stay ----


class Ambiguous(Exception):
    pass


_KEY = re.compile(r"([A-Za-z_][A-Za-z0-9_.-]*)[ \t]*:(?:[ \t]+(.*)|)$")
_ITEM = re.compile(r"(\s*)-[ \t]+(.*)$")
_FALSE_WORDS = {"false", "False", "FALSE", "no", "No", "NO", "off", "Off", "OFF"}
_BOOL_WORDS = _FALSE_WORDS | {"true", "True", "TRUE", "yes", "Yes", "YES", "on", "On", "ON"}


def _content(line):
    stripped = line.strip()
    return bool(stripped) and not stripped.startswith("#")


def _indent(line):
    return len(line) - len(line.lstrip(" "))


def _value_and_comment(rest):
    """A plain value and its trailing comment (with the space before it), as written. A # starts a comment only outside
    quotes and after a space, as YAML reads it."""
    rest = rest or ""
    quote = None
    for index, character in enumerate(rest):
        if quote:
            if character == quote:
                quote = None
        elif character in "'\"":
            quote = character
        elif character == "#" and (index == 0 or rest[index - 1] in " 	"):
            start = index
            while start > 0 and rest[start - 1] in " 	":
                start -= 1
            return rest[:start].strip(), " " + rest[index:] if start == index else rest[start:]
    return rest.strip(), ""


def _scalar_name(value):
    """An environment variable's name written as a plain or simply quoted scalar, or None."""
    if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
        value = value[1:-1]
    return value if _NAME.fullmatch(value) else None


def _one_line(rest, where):
    """A value must end on its own line: a quoted or flow value running onto the next lines could hold text that reads
    as a key."""
    value, _ = _value_and_comment(rest)
    if value[:1] in ("'", '"') and not (len(value) >= 2 and value.endswith(value[0])):
        raise Ambiguous("a value under %s runs over several lines" % where)
    if value[:1] in ("[", "{") and not value.endswith("]" if value[0] == "[" else "}"):
        raise Ambiguous("a value under %s runs over several lines" % where)


def _top_level(lines):
    """{key: line index} of the top-level mapping, refusing what a text edit can't read with certainty."""
    keys = {}
    seen_content = False
    for index, line in enumerate(lines):
        indentation = line[:len(line) - len(line.lstrip(" \t"))]
        if "\t" in indentation and _content(line):
            raise Ambiguous("it uses tabs for indentation")
        if not _content(line):
            continue
        stripped = line.rstrip()
        if stripped.startswith("%"):
            raise Ambiguous("it has a YAML directive")
        if re.fullmatch(r"(---|\.\.\.)(\s+#.*)?", stripped) or stripped.startswith("--- "):
            if seen_content or stripped.startswith("..."):
                raise Ambiguous("it holds more than one YAML document")
            seen_content = True
            continue
        seen_content = True
        if _indent(line) == 0:
            match = _KEY.fullmatch(stripped)
            if not match:
                raise Ambiguous("a top-level line isn't a plain key (line %d)" % (index + 1))
            if match.group(1) in keys:
                raise Ambiguous("the key %s appears twice at the top level" % match.group(1))
            _one_line(match.group(2), "the top level")
            keys[match.group(1)] = index
    return keys


def _block_end(lines, start):
    """The index after the last content line of the block that starts at ``start``."""
    end = start + 1
    for index in range(start + 1, len(lines)):
        if _content(lines[index]):
            if _indent(lines[index]) == 0:
                break
            end = index + 1
    return end


def _children(lines, start, end, parent):
    """(child indent or None, {child key: index}) of a block mapping, refusing anything else at the child level."""
    indent = None
    keys = {}
    empty_value = False
    for index in range(start + 1, end):
        line = lines[index]
        if not _content(line):
            continue
        if indent is None:
            indent = _indent(line)
        if _indent(line) < indent:
            raise Ambiguous("%s's lines aren't evenly indented" % parent)
        if _indent(line) == indent:
            # A list may sit at its key's own indentation ("key:" then "- item"): its items belong to that key.
            if line.lstrip().startswith("- ") and empty_value:
                continue
            match = _KEY.fullmatch(line.strip())
            if not match:
                raise Ambiguous("%s holds something other than plain keys (line %d)" % (parent, index + 1))
            if match.group(1) in keys:
                raise Ambiguous("%s.%s appears twice" % (parent, match.group(1)))
            _one_line(match.group(2), parent)
            keys[match.group(1)] = index
            empty_value = not _value_and_comment(match.group(2))[0]
    return indent, keys


def _check_inline(line, key):
    value, _ = _value_and_comment(_KEY.fullmatch(line.strip()).group(2))
    if value:
        raise Ambiguous("%s: has a value on its own line, not a block under it" % key)


def plan_config(text, newline):
    """The config.yaml text with the two settings in place, and what changed; raises Ambiguous to refuse."""
    lines = (text or "").splitlines()
    keys = _top_level(lines)
    changes = []
    unit = 2

    def add_block(block):
        while lines and not lines[-1].strip():
            lines.pop()
        if lines:
            lines.append("")
        lines.extend(block)

    # terminal.env_passthrough gets MESSAGE_LINK_REPORTS, and loses MESSAGELINK_CODE: an earlier setup listed it, and
    # Hermes refuses it there (a platform secret), with a warning in its log.
    if "terminal" not in keys:
        add_block(["terminal:", " " * unit + "env_passthrough:", " " * unit * 2 + "- " + COMMAND_ENV])
        changes.append("passthrough")
    else:
        start = keys["terminal"]
        _check_inline(lines[start], "terminal")
        end = _block_end(lines, start)
        indent, children = _children(lines, start, end, "terminal")
        if indent is None:
            indent = unit
        if "env_passthrough" not in children:
            lines[start + 1:start + 1] = [" " * indent + "env_passthrough:", " " * (indent + unit) + "- " + COMMAND_ENV]
            changes.append("passthrough")
        else:
            at = children["env_passthrough"]
            value, comment = _value_and_comment(_KEY.fullmatch(lines[at].strip()).group(2))
            if value.startswith("["):
                if not value.endswith("]") or any(c in value[1:-1] for c in "[]{}#"):
                    raise Ambiguous("terminal.env_passthrough is a list this setup can't read")
                inner = value[1:-1].strip()
                items = [item.strip() for item in inner.split(",")] if inner else []
                names = [_scalar_name(item) for item in items]
                if any(name is None for name in names):
                    raise Ambiguous("terminal.env_passthrough holds something other than names")
                kept = [item for item, name in zip(items, names) if name != CODE_ENV]
                if COMMAND_ENV not in names:
                    kept.append(COMMAND_ENV)
                if kept != items:
                    lines[at] = " " * indent + "env_passthrough: [" + ", ".join(kept) + "]" + comment
                    changes.append("passthrough")
            elif value:
                raise Ambiguous("terminal.env_passthrough isn't a list")
            else:
                # A block list under it, at its own indentation or deeper.
                item_indent = None
                last = at
                names = []
                stale = []
                for index in range(at + 1, end):
                    line = lines[index]
                    if not _content(line):
                        continue
                    if _indent(line) < indent or (_indent(line) == indent and not line.lstrip().startswith("-")):
                        break
                    match = _ITEM.fullmatch(line.rstrip())
                    if not match or (item_indent is not None and len(match.group(1)) != item_indent):
                        raise Ambiguous("terminal.env_passthrough holds something other than a list of names")
                    item_indent = len(match.group(1))
                    item, _ = _value_and_comment(match.group(2))
                    name = _scalar_name(item)
                    if name is None:
                        raise Ambiguous("terminal.env_passthrough holds something other than names")
                    names.append(name)
                    if name == CODE_ENV:
                        stale.append(index)
                    last = index
                if COMMAND_ENV not in names:
                    lines.insert(last + 1, " " * (item_indent if item_indent is not None else indent + unit) + "- " + COMMAND_ENV)
                    changes.append("passthrough")
                # After the insert, which comes below every item: these indexes are still the items'.
                for index in reversed(stale):
                    del lines[index]
                if stale and "passthrough" not in changes:
                    changes.append("passthrough")

    # messagelink.gateway_restart_notification: false.
    keys = _top_level(lines)
    if "messagelink" not in keys:
        add_block(["messagelink:", " " * unit + "gateway_restart_notification: false"])
        changes.append("notices")
    else:
        start = keys["messagelink"]
        _check_inline(lines[start], "messagelink")
        end = _block_end(lines, start)
        indent, children = _children(lines, start, end, "messagelink")
        if indent is None:
            indent = unit
        if "gateway_restart_notification" not in children:
            lines.insert(start + 1, " " * indent + "gateway_restart_notification: false")
            changes.append("notices")
        else:
            at = children["gateway_restart_notification"]
            value, comment = _value_and_comment(_KEY.fullmatch(lines[at].strip()).group(2))
            if value not in _BOOL_WORDS:
                raise Ambiguous("messagelink.gateway_restart_notification isn't true or false")
            if value not in _FALSE_WORDS:
                lines[at] = " " * indent + "gateway_restart_notification: false" + comment
                changes.append("notices")

    return newline.join(lines) + newline, changes


def verify_with_yaml(before, after):
    """With PyYAML (Hermes's own), the edit must read back as the old settings plus the two changes, nothing else."""
    try:
        import yaml
    except ImportError:
        return
    try:
        old = yaml.safe_load(before) if before and before.strip() else {}
        new = yaml.safe_load(after)
    except yaml.YAMLError:
        raise Ambiguous("it isn't valid YAML") from None
    if old is None:
        old = {}
    if not isinstance(old, dict):
        raise Ambiguous("it isn't a mapping of settings")
    expected = json.loads(json.dumps(old, default=str))
    terminal = expected.get("terminal") or {}
    passthrough = [name for name in terminal.get("env_passthrough") or [] if name != CODE_ENV]
    if COMMAND_ENV not in passthrough:
        passthrough.append(COMMAND_ENV)
    terminal["env_passthrough"] = passthrough
    expected["terminal"] = terminal
    messagelink = expected.get("messagelink") or {}
    messagelink["gateway_restart_notification"] = False
    expected["messagelink"] = messagelink
    if json.loads(json.dumps(new, default=str)) != expected:
        raise Ambiguous("a text edit would change more than the two settings")


def config_edit(path):
    """(new text or None when nothing changes, what changed, the old text); Stop when it can't be done safely."""
    text = read_text(path)
    newline = "\r\n" if text and "\r\n" in text else "\n"
    try:
        new, changes = plan_config(text or "", newline)
        if changes:
            verify_with_yaml(text or "", new)
    except Ambiguous as reason:
        raise Stop("config.yaml wasn't changed: %s. Add these to it by hand (and take %s out of env_passthrough), "
                   "then run the command again:\n"
                   "  terminal:\n    env_passthrough:\n      - %s\n"
                   "  messagelink:\n    gateway_restart_notification: false" % (reason, CODE_ENV, COMMAND_ENV), 2) from None
    return (new if changes else None), changes, text


# ---- Hermes's command ----


def hermes_command(given):
    if given:
        return given
    beside = os.path.dirname(sys.executable)
    for name in ("hermes.exe", "hermes") if os.name == "nt" else ("hermes",):
        candidate = os.path.join(beside, name)
        if os.path.isfile(candidate) and os.access(candidate, os.X_OK):
            return candidate
    import shutil
    found = shutil.which("hermes")
    if not found:
        raise Stop("Hermes's own command, hermes, isn't found here. Run this where Hermes runs.", 2)
    return found


def enable_plugin(hermes, profile):
    # Always named, default too: without -p, Hermes uses the profile last chosen with "hermes profile use".
    args = [hermes, "-p", profile, "plugins", "enable", "messagelink"]
    try:
        result = subprocess.run(args, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                timeout=120)
    except (OSError, subprocess.SubprocessError) as error:
        raise Stop("Hermes couldn't enable the plugin (%s)." % type(error).__name__) from None
    if result.returncode != 0:
        said = result.stdout.decode("utf-8", "replace").strip().splitlines()[-5:]
        raise Stop("Hermes couldn't enable the plugin (exit %d)%s" % (result.returncode,
                   (":\n  " + "\n  ".join(said)) if said else "."))


def restart_hermes(hermes):
    """Restarts Hermes's gateway, so commands see the code; whether it restarted.

    Hermes (v0.23.1, tools/env_passthrough.py) reads a profile's terminal.env_passthrough once per process and keeps
    it: a profile that ran a command before setup never passes MESSAGE_LINK_REPORTS to ml-report until the gateway
    restarts. No reload clears that list. ``--all`` restarts the one host gateway that serves every profile when
    Hermes multiplexes them (in Docker, through s6, without stopping the container). Without ``--yes``, Hermes refuses
    while a chat or job is running, so nothing is interrupted. ``--detach`` keeps a gateway with no service manager
    from starting in this process.

    Hermes's own words are never shown: its refusal names the running work (a task's goal, a scheduled job's name).
    Under s6 it reports a gateway it couldn't restart, or none registered, on a line marked U+2717 and still exits 0,
    so that mark anywhere counts as not restarted."""
    args = [hermes, "-p", "default", "gateway", "restart", "--all", "--detach"]
    try:
        result = subprocess.run(args, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                timeout=180)
    except (OSError, subprocess.SubprocessError) as error:
        say("Hermes didn't restart (%s)." % type(error).__name__)
        return False
    said = re.sub(r"\x1b\[[0-9;]*m", "", result.stdout.decode("utf-8", "replace"))
    if "Refusing to restart the gateway" in said:
        say("Hermes didn't restart: a chat or job is running in it.")
        return False
    if result.returncode != 0 or chr(0x2717) in said:
        say("Hermes didn't restart (exit %d)." % result.returncode)
        return False
    return True


# ---- The steps ----


def parse(argv):
    hermes = None
    shared_network = True
    words = []
    index = 0
    while index < len(argv):
        arg = argv[index]
        if arg == "--hermes" and index + 1 < len(argv):
            hermes = argv[index + 1]
            index += 1
        elif arg == "--no-shared-network":
            shared_network = False
        elif arg.startswith("-"):
            raise Stop("Unknown option %s." % arg, 2)
        else:
            words.append(arg)
        index += 1
    if len(words) < 3:
        raise Stop("Run the command the Message Link server's page shows.", 2)
    profile, pin, addresses = words[0], words[1], words[2:]
    if not _PROFILE.fullmatch(profile):
        raise Stop("That isn't a Hermes profile name.", 2)
    if not _SHA256.fullmatch(pin):
        raise Stop("Run the command the Message Link server's page shows (its certificate fingerprint is missing).", 2)
    if any(split_address(address) is None for address in addresses):
        raise Stop("Run the command the Message Link server's page shows (an address in it isn't valid).", 2)
    return profile, pin, addresses, hermes, shared_network


def bundle_files():
    if not BUNDLE:
        raise Stop("This copy of the installer has no files in it. Run the command the server's page shows.", 2)
    files = {}
    with zipfile.ZipFile(io.BytesIO(base64.b64decode(BUNDLE))) as archive:
        for entry in archive.infolist():
            if entry.filename not in PLACES or entry.filename in files:
                raise Stop("The installer's files aren't the expected ones. Nothing changed.", 2)
            files[entry.filename] = archive.read(entry)
    if set(files) != set(PLACES):
        raise Stop("The installer's files aren't the expected ones. Nothing changed.", 2)
    return files


def choose_address(candidates, pin, setup, shared_network):
    refused = []
    for address in candidates:
        try:
            status, response, _ = exchange(address, pin, setup, "GET", PROBE_PATH)
        except Unreachable as error:
            say("  %s: can't connect (%s)." % (address, error))
            continue
        except OtherServer:
            say("  %s: another server answered." % address)
            continue
        except NoAnswer:
            say("  %s: no answer." % address)
            continue
        if status == 204:
            return address
        if status == 429:
            raise Stop("Too many tries with this command. Wait a minute, then run it again.")
        if status == 404:
            refused.append(address)
            say("  %s: the server answered but refused this connection." % address)
            continue
        say("  %s: the server answered %d." % (address, status))
    if refused:
        hint = ("The server refuses connections that don't come straight from the home network. A container that "
                "reaches it through a published port arrives from Docker's own address, which it refuses, or the "
                "command has expired or was replaced.")
    else:
        hint = "Hermes can't reach the server at any of these addresses."
    fix = ("If Hermes and Message Link run in Docker on the same computer, put both containers on one Docker network "
           "(not the default bridge), then run the command again.")
    if not shared_network:
        fix = "The Hermes and Message Link containers don't share a Docker network. " + fix
    raise Stop(hint + " " + fix, 2)


def token_for(folder, setup):
    """This setup's token: the one a run of this same command already made (its answer was lost), else a new one."""
    setup_hash = hashlib.sha256(setup.encode("ascii")).hexdigest()
    path = os.path.join(folder, PENDING)
    try:
        with open(path, "r", encoding="utf-8") as file:
            kept = json.load(file)
        if kept.get("setup") == setup_hash and re.fullmatch(r"mlr1_[A-Za-z0-9_-]{43}", kept.get("token", "")):
            return kept["token"], path
    except (OSError, ValueError, AttributeError):
        pass
    token = "mlr1_" + base64.urlsafe_b64encode(secrets.token_bytes(32)).decode("ascii").rstrip("=")
    write_atomic(path, json.dumps({"setup": setup_hash, "token": token}).encode("utf-8"), 0o600)
    return token, path


def redeem(address, pin, setup, token):
    body = json.dumps({"token_sha256": hashlib.sha256(token.encode("ascii")).hexdigest(), "address": address}).encode()
    for attempt in range(3):
        try:
            status, _, _ = exchange(address, pin, setup, "POST", REDEEM_PATH, body)
        except (Unreachable, OtherServer, NoAnswer):
            # The same token's hash again is safe: the server finishes the same setup or says it was already done.
            if attempt < 2:
                time.sleep(1)
                continue
            raise Stop("No answer from the server, so reports may or may not be on. Run the same command again "
                       "within 10 minutes to finish.") from None
        if status == 200:
            return
        if status == 409:
            raise Stop("This command was already used. Make a new one on the server's page, which shows where it was "
                       "used.")
        if status == 404:
            raise Stop("The server doesn't know this command any more: it works for 10 minutes, and a newer one "
                       "replaces it. Make a new one on the server's page.")
        if status == 503:
            raise Stop("The server isn't ready. Run the command again in a minute.")
        raise Stop("The server answered %d. Nothing in Hermes changed." % status)


def check(address, pin, token):
    try:
        status, _, _ = exchange(address, pin, token, "GET", CHECK_PATH)
    except (Unreachable, OtherServer, NoAnswer):
        raise Stop("Everything is in place, but the check couldn't reach the server. Run ml-report check from the "
                   "profile's terminal later.") from None
    if status != 204:
        raise Stop("Everything is in place, but the server refused the check (%d)." % status)


def run(argv):
    setup = os.environ.pop(SETUP_ENV, "").strip()
    profile, pin, addresses, hermes, shared_network = parse(argv)
    if not _SETUP.fullmatch(setup):
        raise Stop("The setup code is missing. Run the command the Message Link server's page shows.", 2)
    folder = profile_folder(profile)
    if not os.path.isdir(folder):
        raise Stop("Hermes has no profile named %s here (no folder %s)." % (profile, folder), 2)
    owner_check(folder)
    say("Setting up Message Link reports for the Hermes profile %s." % profile)
    with ProfileLock(folder):
        install(folder, profile, setup, pin, addresses, hermes, shared_network)
    return 0


class ProfileLock:
    """One setup at a time for a profile: two at once could each make a token and leave the other's in .env."""

    def __init__(self, folder):
        self.path = os.path.join(folder, PENDING + ".lock")
        self.file = None

    def __enter__(self):
        self.file = open(self.path, "a+b")
        try:
            if os.name == "nt":
                import msvcrt
                self.file.seek(0)
                msvcrt.locking(self.file.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            self.file.close()
            raise Stop("Another setup is running for this profile. Wait for it to finish, then run the command again.")
        return self

    def __exit__(self, *details):
        # Closing the file lets go of the lock. The empty file stays: removing it could let a third run lock another.
        self.file.close()


def install(folder, profile, setup, pin, addresses, hermes, shared_network):
    # Everything that could refuse is checked before anything changes.
    files = bundle_files()
    env_path = os.path.join(folder, ".env")
    config_path = os.path.join(folder, "config.yaml")
    for path in (env_path, config_path):
        plain_file(path)
    env_text = read_text(env_path)
    env_with_code(env_text, "")
    config_edit(config_path)
    hermes = hermes_command(hermes)

    candidates = []
    for address in [code_address(env_code(env_text))] + addresses:
        if address and address not in candidates:
            candidates.append(address)
    say("Looking for the server:")
    address = choose_address(candidates, pin, setup, shared_network)
    say("  Hermes reaches the server at %s." % address)

    token, pending = token_for(folder, setup)
    redeem(address, pin, setup, token)
    say("The server took the new code. Any earlier code for this profile stopped working.")

    # From here the server has the new code. The token waits in the profile until the end, so running the same command
    # again finishes the setup with it.
    try:
        for name, places in PLACES.items():
            for place in places:
                write_atomic(os.path.join(folder, *place.split("/")), files[name], 0o755 if name in EXECUTABLE else 0o644)
        say("Files: scripts/ml-report, skills/message-link-reports/SKILL.md and plugins/messagelink/.")

        write_atomic(env_path, env_with_code(read_text(env_path), build_code(address, pin, token)).encode("utf-8"),
                     private_mode(env_path))
        say(".env: %s and %s set." % OURS)

        # The settings go in before the plugin is enabled, so it never runs without the restart notices off; enabling
        # may write config.yaml itself, so they are checked again after it.
        settled = configure(config_path)
        enable_plugin(hermes, profile)
        say("Plugin enabled.")
        settled = configure(config_path) or settled
        say("config.yaml: " + ("commands can see %s, and Hermes's restart notices stay out of Message Link." % COMMAND_ENV
                               if settled else "already set."))

        check(address, pin, token)
        say("Check passed.")
    except Stop as stop:
        raise Stop(str(stop) + " The server already has this profile's new code: run the same command again to finish.",
                   stop.exit_code) from None
    except OSError as error:
        raise Stop("Setup stopped: %s (%s). The server already has this profile's new code: run the same command again "
                   "to finish." % (error.strerror or type(error).__name__, os.path.basename(error.filename or ""))) from None
    try:
        os.unlink(pending)
    except OSError:
        pass
    say("Restarting Hermes so %s can use the new settings." % profile)
    if restart_hermes(hermes):
        say("Hermes restarted.")
        say("Done. %s can now message you first." % profile)
    else:
        say("Setup is done. Restart Hermes when nothing is running in it, or restart its container. Until then, %s "
            "can't message you first." % profile)


def configure(config_path):
    """Writes the two settings into config.yaml when they aren't there; whether it changed anything."""
    plain_file(config_path)
    new, changes, _ = config_edit(config_path)
    if new is not None:
        write_atomic(config_path, new.encode("utf-8"), mode_of(config_path, 0o644))
    return bool(changes)


def main(argv):
    try:
        return run(argv)
    except Stop as stop:
        say(str(stop))
        return stop.exit_code
    except KeyboardInterrupt:
        say("Stopped.")
        return 1
    except OSError as error:
        say("Setup stopped: %s (%s)." % (error.strerror or type(error).__name__, os.path.basename(error.filename or "")))
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
