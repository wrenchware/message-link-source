# Copyright (c) 2026 Wrenchware. MIT License; see LICENSE.
"""Message Link delivery for Hermes: a platform that only sends.

``hermes send --to messagelink "..."`` and scheduled jobs with ``deliver=messagelink`` post into this profile's
conversation in Message Link, through the server's report drop box (``mlreport_client.py``, shared with ml-report).
Nothing comes in through this platform: chatting with Hermes stays in Message Link.

Settings, per profile (its ``.env``): ``MESSAGELINK_CODE``, the connection code from the server's page, and
``MESSAGELINK_HOME_CHANNEL``, where scheduled jobs deliver (``home``; the code alone picks the conversation, so any
chat id lands in the same place). Every setting is read through Hermes's profile-scoped secret reader, never the
process environment, so each profile of a shared gateway uses its own code.

Hermes passes no delivery id to a platform (``send``'s metadata carries the job id for scheduled jobs, which is the
same on every run, and the out-of-process sender gets none), so reports are sent without an idempotency key. A key
made from the text would merge two real reports that happen to say the same thing, so none is made.

So nothing that may have arrived is ever sent again. Nothing here retries, and the plugin takes the whole message:
it registers no length for Hermes to split at (Hermes would send each piece as its own call, each allowed five
reports), and declares that it splits long messages itself (Hermes would cut a scheduled job's output at 4,000
characters). It splits a message at a line break or a space into at most five reports, refused whole before any is
sent when it needs more, and sends them in order, stopping at the first failure.

When a failure leaves a report that may have arrived (no answer came back, or earlier parts were delivered), the
adapter marks its failed result as partly delivered (``raw_response["partial_overflow"]``). Hermes's delivery router
then raises PartialDeliveryError, which a scheduled job records as an error without falling back to the
out-of-process sender, and Hermes's own send retry never resends a partly delivered result. A failure that left
nothing behind (refused, unreachable, wrong certificate) stays a plain failure, so a scheduled job's fallback may try
once more, which can't duplicate anything.

Two other Hermes senders resend a failed result later, and don't read that mark. Hermes's restart notices ("gateway
online" to every home channel after a planned restart, and the restart and shutdown notices) leave a home channel
pending until a send succeeds and send it again when an adapter reconnects; a kanban notification subscribed to a
home channel rewinds and is sent again on the next tick. So the adapter turns Hermes's restart notices off for this
platform (``gateway_restart_notification``, the switch Hermes reads for each platform), and when one of those two
callers still meets a report that may have arrived, it reports the send as done, with a warning in Hermes's log.
"""

import asyncio
import dataclasses
import logging
import sys

from gateway.config import Platform
from gateway.platforms._shared import extra_or_secret, get_scoped_secret, seed_extra_from_env
from gateway.platforms.base import BasePlatformAdapter, SendResult

from . import mlreport_client as client

logger = logging.getLogger(__name__)

NAME = "messagelink"
LABEL = "Message Link"
CODE_ENV = "MESSAGELINK_CODE"
HOME_ENV = "MESSAGELINK_HOME_CHANNEL"
HOME = "home"
MAX_MESSAGE_LENGTH = client.MAX_TEXT
MAX_PARTS = 5
# Hermes modules that send a failed result again later without reading partial_overflow (as of v0.23.1): the restart
# notices' pending home channels (gateway/run_notifications.py) and kanban's notifier (gateway/kanban_watchers_notifier.py).
RESENDING_CALLERS = frozenset({"gateway.run_notifications", "gateway.kanban_watchers_notifier"})


def _code(extra):
    """The connection code: the profile's MESSAGELINK_CODE, else the code seeded into the platform's extra."""
    return str(extra_or_secret(extra or {}, "code", CODE_ENV, "") or "").strip()


def _units(character):
    """(UTF-16 units, UTF-8 bytes) of one character, the two measures the server limits."""
    point = ord(character)
    return (2 if point > 0xFFFF else 1,
            1 if point < 0x80 else 2 if point < 0x800 else 3 if point <= 0xFFFF else 4)


def _fitting(text):
    """How many leading characters of ``text`` fit in one report: 8,000 UTF-16 units and the body limit in bytes."""
    utf16 = utf8 = 0
    for index, character in enumerate(text):
        more16, more8 = _units(character)
        utf16 += more16
        utf8 += more8
        if utf16 > client.MAX_TEXT or utf8 > client.MAX_BODY:
            return index
    return len(text)


def split(text):
    """The reports a message becomes: one when it fits, else parts cut at a line break or a space. Empty for no text."""
    rest = (text or "").strip()
    parts = []
    while rest:
        size = _fitting(rest)
        if size == len(rest):
            parts.append(rest)
            break
        region = rest[:size]
        cut = region.rfind("\n")
        if cut < size // 2:
            cut = region.rfind(" ")
        if cut < 1:
            cut = size
        part = rest[:cut].rstrip()
        if part:
            parts.append(part)
        rest = rest[cut:].lstrip()
    return parts


class PartlyDelivered(client.ReportError):
    """A failure after which something may be in Message Link: ``delivered`` of ``total`` reports were acknowledged,
    and the next one failed (with no answer, it may have arrived too). Nothing of this message may be sent again."""

    def __init__(self, message, delivered, total):
        super().__init__(message)
        self.delivered = delivered
        self.total = total


def deliver(code, text, timeout=client.TIMEOUT):
    """Post a message as one report or a few, in order. Returns the last message id (or None).

    Raises PartlyDelivered when something may have arrived, else the ReportError (from mlreport_client) that stopped
    it before anything could have. Never retries.
    """
    client.read_code(code)
    parts = split(text)
    if not parts:
        raise client.RefusedError("there's no text to send (Message Link reports carry text only)")
    if len(parts) > MAX_PARTS:
        raise client.RefusedError("the message is too long: it would be %d reports, and the most is %d"
                                  % (len(parts), MAX_PARTS))
    last = None
    for number, part in enumerate(parts, 1):
        try:
            last = client.send(code, part, timeout=timeout).id
        except client.UncertainError as error:
            if len(parts) == 1:
                raise PartlyDelivered(str(error), 0, 1) from None
            raise PartlyDelivered("part %d of %d may not have been delivered, after the first %d were; the rest "
                                  "weren't sent: %s" % (number, len(parts), number - 1, error),
                                  number - 1, len(parts)) from None
        except client.ReportError as error:
            if number == 1:
                raise
            raise PartlyDelivered("part %d of %d wasn't delivered, after the first %d were: %s"
                                  % (number, len(parts), number - 1, error), number - 1, len(parts)) from None
    return last


def _called_by_resending_caller():
    """Whether a Hermes module that resends failed results is anywhere up this send's call chain (an awaited
    coroutine's frame leads back to the coroutine awaiting it, so the chain names the caller across awaits)."""
    frame = sys._getframe(1)
    for _ in range(64):
        if frame is None:
            return False
        if frame.f_globals.get("__name__") in RESENDING_CALLERS:
            return True
        frame = frame.f_back
    return False


class MessageLinkAdapter(BasePlatformAdapter):
    """Send only: connect() tests the code against its server, send() posts reports."""

    MAX_MESSAGE_LENGTH = MAX_MESSAGE_LENGTH
    # send() splits a long message itself (into at most MAX_PARTS reports), so Hermes's router hands a scheduled job's
    # whole output over instead of cutting it at 4,000 characters (gateway/delivery.py, _cap_oversized_output).
    splits_long_messages = True

    def __init__(self, config):
        super().__init__(config, Platform(NAME))
        # No restart, online or shutdown notices into Message Link: Hermes reads this switch on the platform's config
        # (the same object it built this adapter from) before each one, and replays a notice whose send failed. Hermes
        # builds a fresh config when a profile's .env or config.yaml changes, without making this adapter again, so the
        # setup also puts messagelink.gateway_restart_notification: false in config.yaml (README, step 4).
        try:
            config.gateway_restart_notification = False
        except AttributeError:
            pass

    def _fatal(self, kind, message):
        # Something only the profile's settings can fix: stop rather than reconnect. Older cores lack the hook.
        setter = getattr(self, "_set_fatal_error", None)
        if setter is not None:
            setter(kind, message, retryable=False)

    async def connect(self, *, is_reconnect=False):
        code = _code(getattr(self.config, "extra", None))
        try:
            client.read_code(code)
        except client.CodeError as error:
            logger.warning("[%s] %s", NAME, error)
            self._fatal("messagelink_code", "%s. Check %s in this profile's .env." % (error, CODE_ENV))
            return False
        try:
            await asyncio.to_thread(client.check, code)
        except client.PinError as error:
            logger.warning("[%s] %s", NAME, error)
            self._fatal("messagelink_pin", str(error))
            return False
        except client.ReportError as error:
            logger.warning("[%s] Can't use Message Link yet: %s", NAME, error)
            return False
        self._mark_connected()
        logger.info("[%s] Connected", NAME)
        return True

    async def disconnect(self):
        self._mark_disconnected()

    async def send(self, chat_id, content, reply_to=None, metadata=None):
        # ``metadata`` holds no delivery id (see the module's note), so no idempotency key is sent.
        code = _code(getattr(self.config, "extra", None))
        try:
            message_id = await asyncio.to_thread(deliver, code, content)
        except PartlyDelivered as error:
            if _called_by_resending_caller():
                # This caller would send the whole message again on a failed result; reported as done instead.
                logger.warning("[%s] May not have been delivered; reported as sent so it isn't sent again: %s",
                               NAME, error)
                return SendResult(success=True, message_id=None, raw_response={
                    "uncertain": True, "delivered_chunks": error.delivered, "total_chunks": error.total})
            logger.warning("[%s] Not delivered, and not to be sent again: %s", NAME, error)
            # Hermes's partly-delivered contract (SendResult.raw_response["partial_overflow"]): its router raises
            # PartialDeliveryError, so a scheduled job doesn't fall back to the out-of-process sender, and its send
            # retry doesn't resend. With 0 delivered it still applies: the one report sent may have arrived.
            return SendResult(success=False, error=str(error), raw_response={
                "partial_overflow": True, "delivered_chunks": error.delivered, "total_chunks": error.total})
        except client.ReportError as error:
            logger.warning("[%s] Not delivered: %s", NAME, error)
            return SendResult(success=False, error=str(error))
        return SendResult(success=True, message_id=message_id)

    def _send_retry_is_final(self, result):
        # A failed report is never sent again by Hermes's own retry: it may have arrived, and a 429 means wait.
        return True

    async def get_chat_info(self, chat_id):
        return {"name": LABEL, "type": "dm", "chat_id": chat_id}


async def send(pconfig, chat_id, message, *, thread_id=None, media_files=None, force_document=False):
    """Out-of-process delivery for ``hermes send`` and scheduled jobs when no gateway is running.

    Returns {"success": True, ...} or {"error": "..."}. Text only: attached files are not sent.
    """
    code = _code(getattr(pconfig, "extra", None))
    if not code:
        return {"error": "Message Link: %s isn't set for this profile" % CODE_ENV}
    try:
        message_id = await asyncio.to_thread(deliver, code, message)
    except client.ReportError as error:
        return {"error": "Message Link: %s" % error}
    return {"success": True, "platform": NAME, "chat_id": chat_id or HOME, "message_id": message_id}


def check_requirements():
    """Standard library only, so the one requirement is a code for this profile."""
    return bool(str(get_scoped_secret(CODE_ENV, "") or "").strip())


def validate_config(config):
    return bool(_code(getattr(config, "extra", None)))


def is_connected(config):
    return validate_config(config)


def _env_enablement():
    """Seeds the platform's extra and home channel from this profile's settings; None without a code."""
    seed = seed_extra_from_env(((CODE_ENV, "code", None),), home_env=HOME_ENV, home_default=HOME)
    return seed if seed.get("code") else None


def _entry_fields():
    """The fields this Hermes accepts on a platform entry, or None when it can't say."""
    try:
        from gateway.platform_registry import PlatformEntry
        return {field.name for field in dataclasses.fields(PlatformEntry)}
    except Exception:
        return None


def register(ctx):
    """Plugin entry point: Hermes calls it when the plugin loads."""
    kwargs = dict(
        name=NAME, label=LABEL, adapter_factory=MessageLinkAdapter, check_fn=check_requirements,
        validate_config=validate_config, required_env=[CODE_ENV],
        install_hint="Add %s, the connection code from the Message Link server's page, to this profile's .env"
                     % CODE_ENV,
    )
    # Older cores refuse fields they don't know, so each later one is passed only when this Hermes has it.
    known = _entry_fields()
    for key, value in (
        ("env_enablement_fn", _env_enablement),
        ("cron_deliver_env_var", HOME_ENV),
        ("standalone_sender_fn", send),
        # 0, "no limit": a length here makes Hermes split before calling the plugin, one call per piece, which would
        # get round the five-report cap and the refusal of a message too long to send whole.
        ("max_message_length", 0),
        ("is_connected", is_connected),
        ("pii_safe", True),
        ("allow_update_command", False),
        ("dashboard_description", "Deliver messages and scheduled job results into Message Link."),
    ):
        if known is None or key in known:
            kwargs[key] = value
    ctx.register_platform(**kwargs)
