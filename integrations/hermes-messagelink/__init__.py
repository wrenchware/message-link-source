# Copyright (c) 2026 Wrenchware. MIT License; see LICENSE.
"""Message Link platform plugin for Hermes. Hermes imports this folder as a package and calls register(ctx)."""

from .adapter import register

__all__ = ["register"]
