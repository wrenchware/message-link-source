"""Offline installer checks. Docker is a stand-in; all host paths are in a temporary directory."""
import io
import contextlib
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import textwrap
import unittest
from unittest.mock import patch
import urllib.error
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / 'docker/messages-server/install-unraid.sh'
WORKFLOW = ROOT / '.github/workflows/messages-server.yml'

DOCKER = r'''#!/usr/bin/env python3
import json, os, pathlib, sys
a = sys.argv[1:]
with open(os.environ['CALLS'], 'a') as log:
    log.write(json.dumps(a) + '\n')
failure = os.environ.get('FAIL_AT', '')
if a[0] == failure:
    sys.exit(1)
if a[0] == 'ps' and failure == 'port':
    print('another-container|0.0.0.0:8788->8787/tcp')
elif a[0] == 'inspect' and '-f' in a and '.Config.Env' in a[a.index('-f') + 1]:
    print(os.environ.get('INSTALLED_ENV', ''))
elif a[0] == 'inspect' and '-f' in a:
    print(os.environ.get('INSTALLED_IMAGE', 'ghcr.io/wrenchware/messages-server:latest-google'))
elif a[0] == 'create':
    print('synthetic-assets')
elif a[0] == 'cp':
    dest = pathlib.Path(a[-1])
    for name, content in [('assets/app-icon.png', 'icon'), ('docker/messages-server/chrome-seccomp.json', '{}')]:
        if failure == 'missing' and name.endswith('app-icon.png'):
            continue
        target = dest / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content)
elif a[0] == 'logs':
    print('server listening transport=network')
elif a[0] == 'run':
    print('synthetic-server')
'''


class InstallerChecks(unittest.TestCase):
    def run_install(self, *options, bundle=False, failure='', extra=None, saved_template=None):
        with tempfile.TemporaryDirectory(prefix='messages-install-check-') as folder:
            root = Path(folder)
            # Exercise XML escaping and quoted paths too.
            here = root / 'bundle & quoted'
            here.mkdir()
            script = SCRIPT.read_text()
            for host in ['/boot/config/plugins/dockerMan', '/var/lib/docker/unraid/images',
                         '/usr/local/emhttp/state/plugins/dynamix.docker.manager/images']:
                script = script.replace(host, str(root / host.lstrip('/')))
            (here / 'install-unraid.sh').write_text(script)
            commands = root / 'commands'
            commands.mkdir()
            stubs = {
                'docker': DOCKER,
                'chown': '#!/bin/sh\nexit 0\n',
                'ss': '#!/bin/sh\nexit 0\n',
                'cat': '#!/bin/sh\ncase "$1" in /proc/sys/user/max_user_namespaces) echo 1000;; /proc/sys/kernel/unprivileged_userns_clone) echo 1;; *) exec /bin/cat "$@";; esac\n',
            }
            for name, source in stubs.items():
                target = commands / name
                target.write_text(source)
                target.chmod(0o755)
            if bundle:
                with tarfile.open(here / 'messages-source.tar.gz', 'w:gz') as archive:
                    for name in ['assets/app-icon.png', 'docker/messages-server/chrome-seccomp.json']:
                        data = b'fixture'
                        info = tarfile.TarInfo(name)
                        info.size = len(data)
                        archive.addfile(info, io.BytesIO(data))
            # No ambient Docker settings, provider sessions or registry token are used.
            env = {
                'PATH': str(commands) + ':/usr/bin:/bin', 'CALLS': str(root / 'calls'),
                'FAIL_AT': failure, 'MESSAGES_APPDATA': str(root / 'data & \'quoted\' "double"'),
                'MESSAGES_BACKUPS_DIR': str(root / 'backups share'),
                'MESSAGES_BIND_IP': '192.0.2.10', 'MESSAGES_PORT': '8788',
                'MESSAGES_HOSTNAMES': 'server.example,192.0.2.10', 'TZ': 'Etc/UTC',
            }
            env.update(extra or {})
            if saved_template is not None:
                seeded = root / 'boot/config/plugins/dockerMan/templates-user/my-messages-server.xml'
                seeded.parent.mkdir(parents=True, exist_ok=True)
                seeded.write_text(saved_template)
            result = subprocess.run(['bash', str(here / 'install-unraid.sh'), *options],
                                    env=env, capture_output=True, text=True, timeout=15)
            calls = [json.loads(line) for line in (root / 'calls').read_text().splitlines()] if (root / 'calls').exists() else []
            template = root / 'boot/config/plugins/dockerMan/templates-user/my-messages-server.xml'
            saved = ET.parse(template).getroot() if template.exists() else None
            if saved is not None:
                profiles = list((root / 'boot/config/plugins/dockerMan/messages-server').glob('*.json'))
                if '--chrome-sandbox' in options:
                    self.assertEqual(len(profiles), 1)
                    self.assertIn(str(profiles[0]), saved.findtext('ExtraParams'))
            return result, calls, saved

    def test_local_is_default_and_requires_archive(self):
        result, calls, template = self.run_install()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('No messages-source.tar.gz', result.stderr)
        self.assertFalse(calls)
        for options, image in [((), 'messages-server:local')]:
            with self.subTest(options=options):
                result, calls, template = self.run_install(*options, bundle=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertTrue(any(call[0] == 'build' and image in call for call in calls))
                self.assertFalse(any(call[0] == 'pull' for call in calls))
                self.assertIsNone(template)

    def test_ghcr_variants_keep_runtime_settings_and_template(self):
        for options, tag in [((), 'latest'), (('--chrome-sandbox',), 'latest')]:
            with self.subTest(options=options):
                result, calls, template = self.run_install('--ghcr', *options,
                    extra={'MESSAGES_MEMORY': '5g', 'MESSAGES_PIDS_LIMIT': '2345', 'MESSAGES_CPUS': '1.5',
                           'MESSAGES_ADMIN_PASSWORD': 'page & "secret" 123'})
                self.assertEqual(result.returncode, 0, result.stderr)
                image = 'ghcr.io/wrenchware/messages-server:' + tag
                self.assertIn(['pull', image], calls)
                self.assertFalse(any(call[0] == 'build' for call in calls))
                create = next(call for call in calls if call[0] == 'create')
                self.assertIn('/bin/true', create)
                self.assertIn('none', create)
                self.assertFalse(any('/data' in arg or '/keys' in arg for arg in create))
                self.assertLess(next(i for i, call in enumerate(calls) if call[0] == 'cp'),
                                next(i for i, call in enumerate(calls) if call[0] == 'stop'))
                run = next(call for call in calls if call[:2] == ['run', '-d'])
                self.assertEqual(run[-1], image)
                for word in ['no-new-privileges:true', 'ALL', '60', '5g', '2345', '1.5',
                             '192.0.2.10:8788:8787', 'messages-server-keys:/keys']:
                    self.assertIn(word, run)
                self.assertEqual(template.findtext('Repository'), image)
                self.assertEqual(template.findtext('Privileged'), 'false')
                configs = {item.get('Target'): item.text for item in template.findall('Config')}
                # The server page's password reaches the container by name only (its value is in no command line) and its template, masked there.
                self.assertIn('MESSAGES_ADMIN_PASSWORD', run)
                self.assertFalse(any('secret' in arg for call in calls for arg in call))
                self.assertEqual(configs['MESSAGES_ADMIN_PASSWORD'], 'page & "secret" 123')
                self.assertEqual(next(item for item in template.findall('Config') if item.get('Target') == 'MESSAGES_ADMIN_PASSWORD').get('Mask'), 'true')
                self.assertEqual(configs['MESSAGES_RESTORE'], '1')
                self.assertEqual(configs['TZ'], 'Etc/UTC')
                self.assertEqual(configs['MESSAGES_HOSTNAMES'], 'server.example,192.0.2.10')
                self.assertIn('data & \'quoted\' "double"', configs['/data'])
                # The server's own backups: a folder of their own, mapped to /backups in the container and in its template.
                self.assertTrue(configs['/backups'].endswith('backups share'))
                self.assertTrue(any(arg.endswith('backups share:/backups') for arg in run))
                self.assertNotIn('MESSAGES_TAILNET_AUTHKEY', run)
                self.assertNotIn('MESSAGES_TAILNET_AUTHKEY', configs)
                parameters = template.findtext('ExtraParams')
                for flag in ['--stop-timeout=60', '--cap-drop=ALL', '--memory=5g', '--memory-swap=5g',
                             '--pids-limit=2345', '--cpus=1.5', '-p 192.0.2.10:8788:8787', 'messages-server-keys:/keys']:
                    self.assertIn(flag, parameters)
                for key in ['MESSAGES_GOOGLE', 'MESSAGES_GOOGLE_CHROME', 'MESSAGES_CHROMIUM_HEADED', 'MESSAGES_NETWORK_ISOLATED']:
                    self.assertEqual(configs[key], '1')
                self.assertEqual('MESSAGES_CHROMIUM_NO_SANDBOX' in configs, '--chrome-sandbox' not in options)

    def test_tailnet_auth_key_goes_by_name_and_is_never_saved(self):
        key = 'tskey-auth-kSynthetic0CNTRL-secret'
        result, calls, template = self.run_install('--ghcr', extra={'MESSAGES_TAILNET': '1', 'MESSAGES_TAILNET_AUTHKEY': key})
        self.assertEqual(result.returncode, 0, result.stderr)
        run = next(call for call in calls if call[:2] == ['run', '-d'])
        self.assertIn('MESSAGES_TAILNET_AUTHKEY', run)
        self.assertFalse(any(key in arg for call in calls for arg in call))
        self.assertNotIn(key, ET.tostring(template, encoding='unicode'))
        self.assertNotIn(key, result.stdout + result.stderr)

    def test_default_publish_is_ipv4_only(self):
        # Without a bind address the port is published on IPv4 alone, so Docker's IPv6 proxy never hides a device's address.
        result, calls, template = self.run_install('--ghcr', extra={'MESSAGES_BIND_IP': ''})
        self.assertEqual(result.returncode, 0, result.stderr)
        run = next(call for call in calls if call[:2] == ['run', '-d'])
        self.assertIn('0.0.0.0:8788:8787', run)
        self.assertIn('-p 0.0.0.0:8788:8787', template.findtext('ExtraParams'))

    def test_reinstall_keeps_the_running_server_page_password(self):
        # Set in Unraid's editor and not in this environment: the running container's is kept, by name only on the command line.
        result, calls, template = self.run_install('--ghcr', extra={'INSTALLED_ENV': 'MESSAGES_RESTORE=1\nMESSAGES_ADMIN_PASSWORD=kept & "from" the editor'})
        self.assertEqual(result.returncode, 0, result.stderr)
        configs = {item.get('Target'): item.text for item in template.findall('Config')}
        self.assertEqual(configs['MESSAGES_ADMIN_PASSWORD'], 'kept & "from" the editor')
        self.assertFalse(any('editor' in arg for call in calls for arg in call))
        self.assertNotIn('editor', result.stdout + result.stderr)
        # No container (an earlier run removed it and then failed): the saved template's is kept, unescaped.
        saved = '<Container><Config Name="Server page password" Target="MESSAGES_ADMIN_PASSWORD" Type="Variable" Mask="true">saved &amp; &quot;kept&quot; &lt;ok&gt;</Config></Container>'
        result, calls, template = self.run_install('--ghcr', saved_template=saved)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual({item.get('Target'): item.text for item in template.findall('Config')}['MESSAGES_ADMIN_PASSWORD'], 'saved & "kept" <ok>')
        self.assertFalse(any('kept' in arg for call in calls for arg in call))
        # A character the template holds as a numeric reference is not guessed at: the page stays off, and the owner is told to enter it again.
        odd = '<Container><Config Name="Server page password" Target="MESSAGES_ADMIN_PASSWORD" Type="Variable" Mask="true">caf&#233; password</Config></Container>'
        result, calls, template = self.run_install('--ghcr', saved_template=odd)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIsNone({item.get('Target'): item.text for item in template.findall('Config')}['MESSAGES_ADMIN_PASSWORD'])
        self.assertIn('enter it again', result.stderr)
        self.assertNotIn('caf', result.stdout + result.stderr)
        # A password set empty, here or in the running container, turns the page off: an older one is never brought back.
        result, calls, template = self.run_install('--ghcr', extra={'INSTALLED_ENV': 'MESSAGES_ADMIN_PASSWORD=the old password', 'MESSAGES_ADMIN_PASSWORD': ''})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIsNone({item.get('Target'): item.text for item in template.findall('Config')}['MESSAGES_ADMIN_PASSWORD'])
        result, calls, template = self.run_install('--ghcr', extra={'INSTALLED_ENV': 'MESSAGES_RESTORE=1\nMESSAGES_ADMIN_PASSWORD='}, saved_template=saved)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIsNone({item.get('Target'): item.text for item in template.findall('Config')}['MESSAGES_ADMIN_PASSWORD'])
        # None anywhere: the page stays off, and the field is there, empty.
        result, calls, template = self.run_install('--ghcr')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual({item.get('Target'): item.text for item in template.findall('Config')}.get('MESSAGES_ADMIN_PASSWORD', 'missing'), None)

    def test_failed_pull_or_assets_leave_running_server_alone(self):
        for failure in ['pull', 'cp', 'missing', 'port']:
            with self.subTest(failure=failure):
                result, calls, template = self.run_install('--ghcr', failure=failure)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(any(call[0] in ['stop', 'run', 'build'] for call in calls))
                self.assertIsNone(template)
                if failure in ['cp', 'missing']:
                    self.assertIn(['rm', '-v', 'synthetic-assets'], calls)

    def test_invalid_options_and_image_fail_before_docker(self):
        for options, extra in [(('--ghcr', '--unknown'), {}),
                               (('--ghcr',), {'MESSAGES_GHCR_IMAGE': 'docker.io/example/messages-server'}),
                               (('--ghcr',), {'MESSAGES_GHCR_TAG': 'bad tag'})]:
            with self.subTest(options=options, extra=extra):
                result, calls, _ = self.run_install(*options, extra=extra)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(calls)

    def test_version_and_other_namespace(self):
        result, calls, template = self.run_install('--ghcr',
            extra={'MESSAGES_GHCR_IMAGE': 'ghcr.io/example/messages-server', 'MESSAGES_GHCR_TAG': '0.1.403',
                   'MESSAGES_BIND_IP': '2001:db8::10'})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(['pull', 'ghcr.io/example/messages-server:0.1.403'], calls)
        self.assertIn('-p [2001:db8::10]:8788:8787', template.findtext('ExtraParams'))

    def test_uninstall_removes_registry_artifacts_and_keeps_data_and_login(self):
        with tempfile.TemporaryDirectory(prefix='messages-uninstall-check-') as folder:
            root = Path(folder)
            script = (SCRIPT.parent / 'uninstall-unraid.sh').read_text()
            for host in ['/boot/config/plugins/dockerMan', '/var/lib/docker/unraid/images',
                         '/usr/local/emhttp/state/plugins/dynamix.docker.manager/images']:
                script = script.replace(host, str(root / host.lstrip('/')))
            target = root / 'uninstall.sh'
            target.write_text(script)
            commands = root / 'commands'
            commands.mkdir()
            docker = commands / 'docker'
            docker.write_text(DOCKER)
            docker.chmod(0o755)
            artifacts = ['boot/config/plugins/dockerMan/templates-user/my-messages-server.xml',
                         'boot/config/plugins/dockerMan/messages-server/chrome-seccomp.json']
            retained = ['data/keep', 'root/.docker/config.json']
            for name in artifacts + retained:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('synthetic')
            env = {'PATH': str(commands) + ':/usr/bin:/bin', 'CALLS': str(root / 'calls'),
                   'MESSAGES_APPDATA': str(root / 'data')}
            result = subprocess.run(['bash', str(target)], env=env, capture_output=True, text=True, timeout=15)
            self.assertEqual(result.returncode, 0, result.stderr)
            calls = [json.loads(line) for line in (root / 'calls').read_text().splitlines()]
            self.assertIn(['image', 'rm', 'ghcr.io/wrenchware/messages-server:latest-google'], calls)
            self.assertFalse(any(call[:2] == ['volume', 'rm'] or call[0] == 'login' for call in calls))
            self.assertTrue(all(not (root / name).exists() for name in artifacts))
            self.assertTrue(all((root / name).exists() for name in retained))


class PublishChecks(unittest.TestCase):
    def python_steps(self):
        blocks = []
        lines = WORKFLOW.read_text().splitlines()
        for i, line in enumerate(lines):
            if line.strip() == "python3 - <<'PY'":
                end = next(j for j in range(i + 1, len(lines)) if lines[j].strip() == 'PY')
                blocks.append(textwrap.dedent('\n'.join(lines[i + 1:end])))
        return blocks

    # Public since 2026-11-11: servers pull with no login, so a private or internal package stops the build, as do auth errors.
    def test_visibility_check_refuses_private_and_auth_errors(self):
        code = compile(self.python_steps()[0], str(WORKFLOW), 'exec')
        for kind in ['User', 'Organization']:
            for outcome in ['private', 'public', 'internal', 404, 401, 403, 500]:
                with self.subTest(kind=kind, outcome=outcome):
                    requests = []
                    def respond(request, timeout):
                        requests.append(request.full_url)
                        if len(requests) == 1:
                            return io.StringIO(json.dumps({'type': kind}))
                        if isinstance(outcome, int):
                            raise urllib.error.HTTPError(request.full_url, outcome, 'synthetic', {}, io.BytesIO())
                        return io.StringIO(json.dumps({'visibility': outcome}))
                    with patch.dict(os.environ, {'GITHUB_REPOSITORY_OWNER': 'example', 'GHCR_TOKEN': 'synthetic'}), \
                         patch('urllib.request.urlopen', respond), patch('sys.stdout', io.StringIO()):
                        if outcome in ['public', 404]:
                            exec(code, {})
                        else:
                            with self.assertRaises(SystemExit):
                                exec(code, {})
                    self.assertIn('/orgs/' if kind == 'Organization' else '/users/', requests[1])

    def test_publishing_is_disabled_without_owner_secret(self):
        lines = WORKFLOW.read_text().splitlines()
        start = next(i for i, line in enumerate(lines) if 'if [ -n "$GHCR_TOKEN" ]' in line)
        end = next(i for i in range(start, len(lines)) if lines[i].strip() == 'fi')
        shell = textwrap.dedent('\n'.join(lines[start:end + 1]))
        for token, enabled in [('', 'false'), ('synthetic', 'true')]:
            with tempfile.TemporaryDirectory() as folder:
                env = {'PATH': '/usr/bin:/bin', 'GHCR_TOKEN': token,
                       'GITHUB_OUTPUT': folder + '/output', 'GITHUB_STEP_SUMMARY': folder + '/summary'}
                result = subprocess.run(['bash', '-eu', '-c', shell], env=env, capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(Path(env['GITHUB_OUTPUT']).read_text(), 'publish=' + enabled + '\n')
                self.assertNotIn('synthetic', result.stdout + result.stderr)

    def test_tag_metadata_and_chrome_refresh(self):
        code = compile(self.python_steps()[1], str(WORKFLOW), 'exec')
        version = ET.parse(ROOT / 'src/NativeBeeper.App/NativeBeeper.App.csproj').findtext('.//Version')
        for suffix in ['', '-google']:
            with tempfile.TemporaryDirectory() as folder:
                env = {'GITHUB_REPOSITORY_OWNER': 'Example', 'GITHUB_SHA': 'a' * 40,
                       'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '2', 'SUFFIX': suffix,
                       'GITHUB_OUTPUT': folder + '/output'}
                with patch.dict(os.environ, env), contextlib.chdir(ROOT):
                    exec(code, {})
                output = Path(env['GITHUB_OUTPUT']).read_text()
                for tag in ['latest', version, 'sha-' + 'a' * 40 + '-run-123-2']:
                    self.assertIn('ghcr.io/example/messages-server:' + tag + suffix + '\n', output)
                self.assertRegex(output, r'refresh=\d{8}T\d{6}Z\n')


if __name__ == '__main__':
    unittest.main(verbosity=2)
