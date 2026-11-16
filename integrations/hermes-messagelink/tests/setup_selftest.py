# Copyright (c) 2026 Wrenchware. MIT License; see LICENSE.
"""Self-test for the setup installer's file handling, standard library only. The server image's build runs it on Linux,
where file modes, links and locks are the real ones; the Windows checks run it too.

    python3 integrations/hermes-messagelink/tests/setup_selftest.py

It checks how setup/install.py rewrites a profile's .env (only the code's lines, whatever else is there, quoted values
over several lines included) and config.yaml (comments kept, anything it can't read with certainty refused), that .env
ends up readable by its owner only, that a linked .env is refused, that one setup at a time holds a profile, and that
setup/install.sh parses in every POSIX shell found here (sh, dash, busybox sh). It is not part of either download.
"""

import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

FOLDER = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# No __pycache__ beside the published installer.
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("ml_setup", os.path.join(FOLDER, "setup", "install.py"))
setup = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(setup)

CODE = "mlrc1.eyJ2IjoxfQ"


def plan(text):
    return setup.plan_config(text, "\n")


class EnvFile(unittest.TestCase):
    def test_only_the_codes_lines_change(self):
        text = "A=1\n  export   MESSAGELINK_CODE =  old\nMESSAGE_LINK_REPORTS='older'\n# MESSAGELINK_CODE=comment\nB=2"
        self.assertEqual(setup.env_with_code(text, CODE), "A=1\n# MESSAGELINK_CODE=comment\nB=2\nMESSAGELINK_CODE=%s\n" % CODE)

    def test_crlf_and_no_final_newline_are_kept(self):
        self.assertEqual(setup.env_with_code("A=1\r\nB=2", CODE), "A=1\r\nB=2\r\nMESSAGELINK_CODE=%s\r\n" % CODE)

    def test_a_quoted_value_over_several_lines_is_left_whole(self):
        text = 'KEY="line one\nMESSAGELINK_CODE=not a setting\nlast"\nC=3\n'
        self.assertEqual(setup.env_with_code(text, CODE), text + "MESSAGELINK_CODE=%s\n" % CODE)
        self.assertIsNone(setup.env_code(text))

    def test_an_escaped_quote_does_not_end_a_value(self):
        # As python-dotenv reads them: in either kind of quotes, a backslash takes the next character into the value.
        for text in ('KEY="a \\" b\nMESSAGELINK_CODE=inside"\n', "KEY='a \\' b\nMESSAGELINK_CODE=inside'\n"):
            with self.subTest(text):
                self.assertEqual(setup.env_with_code(text, CODE), text + "MESSAGELINK_CODE=%s\n" % CODE)
                self.assertIsNone(setup.env_code(text))
        # An escaped backslash, then the closing quote: the next line is a setting of its own.
        for text in ('KEY="a \\\\"\nMESSAGELINK_CODE=old\n', "KEY='a \\\\'\nMESSAGELINK_CODE=old\n"):
            with self.subTest(text):
                self.assertEqual(setup.env_with_code(text, CODE), text.replace("MESSAGELINK_CODE=old", "MESSAGELINK_CODE=%s" % CODE))
                self.assertEqual(setup.env_code(text), "old")

    def test_the_scanner_reads_as_python_dotenv_does(self):
        try:
            import dotenv
        except ImportError:
            self.skipTest("python-dotenv isn't here")
        import io
        for text in ('KEY="a \\" b\nMESSAGELINK_CODE=x"\nZ=1\n', 'KEY="a \\\\"\nMESSAGELINK_CODE=x\nZ=1\n', "KEY='a \\' b\nMESSAGELINK_CODE=x'\nZ=1\n",
                     "KEY='a \\\\'\nMESSAGELINK_CODE=x\nZ=1\n", "KEY='a\nMESSAGELINK_CODE=x'\nZ=1\n", "export  MESSAGELINK_CODE = x\nZ=1\n"):
            with self.subTest(text):
                names = sorted({name for _, name, _ in setup.env_lines(text) if name})
                self.assertEqual(names, sorted(dotenv.dotenv_values(stream=io.StringIO(text))))

    def test_an_unclosed_quote_is_refused(self):
        with self.assertRaises(setup.Stop):
            setup.env_with_code('KEY="never closed\nC=3\n', CODE)

    def test_the_current_code_is_read(self):
        self.assertEqual(setup.env_code("MESSAGE_LINK_REPORTS=old\nMESSAGELINK_CODE='new'\n"), "new")
        self.assertEqual(setup.env_code("MESSAGE_LINK_REPORTS=old\n"), "old")


class ConfigFile(unittest.TestCase):
    def test_comments_and_layout_are_kept(self):
        text = ("# top\nterminal:\n  backend: local  # here\n  env_passthrough:\n    - GITHUB_TOKEN  # git\n"
                "# between\nmessagelink:\n  gateway_restart_notification: true  # was on\n")
        new, changes = plan(text)
        self.assertEqual(new, text.replace("    - GITHUB_TOKEN  # git\n", "    - GITHUB_TOKEN  # git\n    - MESSAGELINK_CODE\n")
                         .replace("notification: true  # was on", "notification: false  # was on"))
        self.assertEqual(changes, ["passthrough", "notices"])
        self.assertEqual(plan(new)[1], [])

    def test_a_flow_list_and_a_list_at_its_keys_indentation(self):
        self.assertIn("env_passthrough: [A, 'B', MESSAGELINK_CODE]  # c", plan("terminal:\n  env_passthrough: [A, 'B']  # c\n")[0])
        self.assertIn("  - A\n  - MESSAGELINK_CODE\n", plan("terminal:\n  env_passthrough:\n  - A\n  backend: x\n")[0])

    def test_what_it_cannot_read_with_certainty_is_refused(self):
        for name, text in [
            ("tabs", "terminal:\n\tbackend: x\n"),
            ("a top-level key twice", "terminal:\n  a: 1\nterminal:\n  b: 2\n"),
            ("a child key twice", "terminal:\n  env_passthrough: [A]\n  env_passthrough: [B]\n"),
            ("the switch twice", "messagelink:\n  gateway_restart_notification: true\n  gateway_restart_notification: true\n"),
            ("a merge key", "terminal:\n  <<: *base\n  backend: x\n"),
            ("a merge key at the top", "<<: *base\nterminal:\n  backend: x\n"),
            ("an inline mapping", "terminal: {backend: local}\n"),
            ("an anchor", "terminal: &t\n  backend: x\n"),
            ("a scalar passthrough", "terminal:\n  env_passthrough: A\n"),
            ("an alias passthrough", "terminal:\n  env_passthrough: *names\n"),
            ("a quoted key", '"terminal":\n  backend: x\n'),
            ("a second document", "a: 1\n---\nterminal:\n  backend: x\n"),
            ("a quoted value over lines", 'terminal:\n  shell: "one\n  env_passthrough: [X]"\n'),
            ("a flow value over lines", "terminal:\n  backend: [a,\n  env_passthrough: b]\n"),
            ("a top-level value over lines", 'note: "one\nterminal:\n  x"\n'),
            ("a switch that isn't a boolean", "messagelink:\n  gateway_restart_notification: maybe\n"),
            ("a passthrough of something else", "terminal:\n  env_passthrough:\n    - {a: 1}\n"),
        ]:
            with self.subTest(name):
                with self.assertRaises(setup.Ambiguous):
                    plan(text)

    def test_with_pyyaml_the_edit_must_read_back_as_the_two_changes(self):
        try:
            import yaml  # noqa: F401
        except ImportError:
            self.skipTest("PyYAML isn't here")
        text = "terminal:\n  env_passthrough:\n    - A\nmessagelink:\n  gateway_restart_notification: true\n"
        setup.verify_with_yaml(text, plan(text)[0])
        with self.assertRaises(setup.Ambiguous):
            setup.verify_with_yaml(text, text + "other: 1\n")


@unittest.skipIf(os.name == "nt", "Unix file modes, links and locks")
class Files(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.mkdtemp(prefix="ml-setup-")

    def tearDown(self):
        shutil.rmtree(self.folder, ignore_errors=True)

    def test_env_ends_up_private(self):
        path = os.path.join(self.folder, ".env")
        with open(path, "w") as file:
            file.write("A=1\n")
        os.chmod(path, 0o644)
        setup.write_atomic(path, b"A=1\n", setup.private_mode(path))
        self.assertEqual(os.stat(path).st_mode & 0o777, 0o600)
        self.assertEqual(setup.private_mode(os.path.join(self.folder, "missing")), 0o600)

    def test_a_linked_env_is_refused(self):
        target = os.path.join(self.folder, "elsewhere")
        open(target, "w").close()
        os.symlink(target, os.path.join(self.folder, ".env"))
        with self.assertRaises(setup.Stop):
            setup.plain_file(os.path.join(self.folder, ".env"))

    def test_one_setup_at_a_time(self):
        with setup.ProfileLock(self.folder):
            child = subprocess.run([sys.executable, "-c", (
                "import importlib.util, sys\n"
                "spec = importlib.util.spec_from_file_location('s', sys.argv[1]); s = importlib.util.module_from_spec(spec)\n"
                "spec.loader.exec_module(s)\n"
                "try:\n    s.ProfileLock(sys.argv[2]).__enter__()\nexcept s.Stop:\n    sys.exit(3)\n"),
                SPEC.origin, self.folder], timeout=10)
            self.assertEqual(child.returncode, 3)
        with setup.ProfileLock(self.folder):
            pass

    def test_the_owner_check(self):
        setup.owner_check(self.folder)


class Shells(unittest.TestCase):
    def test_install_sh_parses_in_every_shell_here(self):
        script = os.path.join(FOLDER, "setup", "install.sh")
        found = [shell for shell in (["sh", "-n"], ["dash", "-n"], ["busybox", "sh", "-n"]) if shutil.which(shell[0])]
        if not found:
            self.skipTest("no POSIX shell here")
        for shell in found:
            with self.subTest(" ".join(shell[:-1])):
                result = subprocess.run(shell + [script], capture_output=True, timeout=10)
                self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
