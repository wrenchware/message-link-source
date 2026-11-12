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

    # The nightly channel (2026-11-12): a build publishes nightly and its exact tag, never a stable tag.
    def test_tag_metadata_and_chrome_refresh(self):
        code = compile(self.python_steps()[1], str(WORKFLOW), 'exec')
        version = ET.parse(ROOT / 'src/NativeBeeper.App/NativeBeeper.App.csproj').findtext('.//Version')
        with tempfile.TemporaryDirectory() as folder:
            env = {'GITHUB_REPOSITORY_OWNER': 'Example', 'GITHUB_SHA': 'a' * 40,
                   'GITHUB_RUN_ID': '123', 'GITHUB_RUN_ATTEMPT': '2', 'GITHUB_OUTPUT': folder + '/output'}
            with patch.dict(os.environ, env), contextlib.chdir(ROOT):
                exec(code, {})
            output = Path(env['GITHUB_OUTPUT']).read_text()
            tags = output.split('tags<<TAGS\n', 1)[1].split('TAGS\n', 1)[0].splitlines()
            self.assertEqual(tags, ['ghcr.io/example/messages-server:nightly',
                                    'ghcr.io/example/messages-server:sha-' + 'a' * 40 + '-run-123-2'])
            self.assertIn('version=' + version + '\n', output)
            self.assertRegex(output, r'refresh=\d{8}T\d{6}Z\n')

    def run_cleanup(self, versions):
        code = compile(self.python_steps()[2], str(WORKFLOW), 'exec')
        deleted = []
        def respond(request, timeout):
            url = request.full_url
            if request.get_method() == 'DELETE':
                deleted.append(int(url.rsplit('/', 1)[1]))
                return io.BytesIO(b'')
            if url.endswith('/users/example'):
                return io.BytesIO(json.dumps({'type': 'User'}).encode())
            return io.BytesIO(json.dumps(versions if url.endswith('page=1') else []).encode())
        with patch.dict(os.environ, {'GITHUB_REPOSITORY_OWNER': 'example', 'GHCR_TOKEN': 'synthetic'}), \
             patch('urllib.request.urlopen', respond), patch('sys.stdout', io.StringIO()):
            try:
                exec(code, {})
            except SystemExit as stopped:
                self.assertEqual(stopped.code, 0)
        return set(deleted)

    # Never the image latest or nightly names, however old; every image of the last ten days; parts only beside an image kept.
    def test_cleanup_keeps_stable_nightly_and_recent_images(self):
        from datetime import datetime, timedelta, timezone
        now = datetime.now(timezone.utc)
        def version(id, days, *tags, hours=0):
            when = (now - timedelta(days=days, hours=hours)).strftime('%Y-%m-%dT%H:%M:%SZ')
            return {'id': id, 'created_at': when, 'metadata': {'container': {'tags': list(tags)}}}
        versions = [
            version(1, 0.1, 'nightly', 'sha-n'), version(2, 0.2, 'sha-b'), version(3, 3, 'sha-c'), version(4, 9, 'sha-d'),
            version(5, 12, 'sha-e'), version(6, 13, 'sha-f'),
            version(7, 30, 'latest', 'latest-google', '0.1.400', '0.1.400-google', 'sha-l'),
            version(8, 40, '0.1.390', 'sha-g'),
            # Parts: one beside the stable image, one beside a removed image, one beside a recent one.
            version(9, 30, hours=0.5), version(10, 13, hours=0.5), version(11, 3, hours=0.2)]
        self.assertEqual(self.run_cleanup(versions), {5, 6, 8, 10})
        # Few images: the newest three stay even when older than ten days, and so does latest.
        few = [version(1, 20, 'nightly', 'sha-n'), version(2, 21, 'sha-b'), version(3, 22, 'sha-c'), version(4, 23, 'sha-d'),
               version(5, 60, 'latest', 'sha-l')]
        self.assertEqual(self.run_cleanup(few), {4})
        # latest and nightly on one image (just promoted), older than the rest.
        same = [version(1, 1, 'sha-a'), version(2, 2, 'sha-b'), version(3, 3, 'sha-c'), version(4, 20, 'sha-x'),
                version(5, 15, 'nightly', 'latest', 'latest-google', 'sha-n')]
        self.assertEqual(self.run_cleanup(same), {4})
        # A listing without latest, or without nightly, removes nothing.
        for missing in ['latest', 'nightly']:
            with self.subTest(missing=missing):
                listing = [version(i, 20 + i, 'sha-' + str(i)) for i in range(1, 8)] + [version(9, 0.1, 'latest' if missing == 'nightly' else 'nightly')]
                self.assertEqual(self.run_cleanup(listing), set())


PROMOTE = ROOT / '.github/scripts/promote-server-image.py'
COMMIT = 'a' * 40
MAIN = 'f' * 40
NIGHTLY = 'sha256:' + 'b' * 64
STABLE = 'sha256:' + 'c' * 64
INDEX = 'application/vnd.oci.image.index.v1+json'


class PromoteChecks(unittest.TestCase):
    """messages-server-promote.yml's script, against a stand-in for GitHub, the registry and git."""

    def module(self):
        import importlib.util
        spec = importlib.util.spec_from_file_location('promote_server_image', PROMOTE)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def world(self):
        return {
            'versions': [
                {'name': NIGHTLY, 'metadata': {'container': {'tags': ['nightly', 'sha-' + COMMIT + '-run-123-1']}}},
                {'name': STABLE, 'metadata': {'container': {'tags': ['latest', 'latest-google', '0.1.400', '0.1.400-google',
                                                                       'sha-' + 'd' * 40 + '-run-99-1', 'sha-' + 'd' * 40 + '-run-99-1-google']}}},
            ],
            'run': {'path': '.github/workflows/messages-server.yml', 'head_branch': 'main', 'head_sha': COMMIT, 'event': 'push',
                    'created_at': '2026-11-10T08:00:00Z'},
            'jobs': [{'name': 'enabled', 'conclusion': 'success'}, {'name': 'image', 'conclusion': 'success'}],
            'compare': 'ahead',
            'project': '<Project><PropertyGroup><Version>0.1.453</Version></PropertyGroup></Project>',
            'registry': {NIGHTLY: (NIGHTLY, INDEX), 'latest': (STABLE, INDEX)},
            'source': 'f00d\trefs/tags/src-' + COMMIT[:12] + '\n',
        }

    def run_script(self, action, world, extra=None):
        module = self.module()
        class Response(io.BytesIO):
            def __init__(self, body=b'', headers=None):
                super().__init__(body)
                self.headers = headers or {}
        def respond(request, timeout):
            url = request.full_url
            def missing():
                raise urllib.error.HTTPError(url, 404, 'synthetic', {}, io.BytesIO())
            if url.startswith('https://ghcr.io/token'):
                return Response(json.dumps({'token': 'pull'}).encode())
            if url.startswith('https://ghcr.io/v2/example/messages-server/manifests/'):
                found = world['registry'].get(url.rsplit('/', 1)[1])
                if found is None:
                    missing()
                return Response(headers={'Docker-Content-Digest': found[0], 'Content-Type': found[1]})
            path = url.removeprefix('https://api.github.com')
            if path == '/users/Example':
                return Response(json.dumps({'type': 'User'}).encode())
            if path.startswith('/users/Example/packages/container/messages-server/versions'):
                return Response(json.dumps(world['versions'] if path.endswith('page=1') else []).encode())
            if path == '/repos/Example/app/actions/runs/123':
                return Response(json.dumps(world['run']).encode()) if world['run'] else missing()
            if path.startswith('/repos/Example/app/actions/runs/123/attempts/1/jobs'):
                return Response(json.dumps({'jobs': world['jobs']}).encode())
            if path == '/repos/Example/app/compare/' + COMMIT + '...' + MAIN:
                return Response(json.dumps({'status': world['compare']}).encode())
            if path == '/repos/Example/app/contents/src/NativeBeeper.App/NativeBeeper.App.csproj?ref=' + COMMIT:
                return Response(world['project'].encode())
            missing()
        def git(arguments, **options):
            self.assertEqual(arguments[:3], ['git', 'ls-remote', '--tags'])
            self.assertEqual(arguments[3], 'https://github.com/example/source.git')
            return subprocess.CompletedProcess(arguments, 0, world['source'], '')
        with tempfile.TemporaryDirectory() as folder:
            env = {'GITHUB_REPOSITORY_OWNER': 'Example', 'GITHUB_REPOSITORY': 'Example/app', 'GITHUB_SHA': MAIN,
                   'GHCR_TOKEN': 'synthetic', 'GH_TOKEN': 'synthetic', 'SOURCE_REPOSITORY': 'example/source',
                   'GITHUB_OUTPUT': folder + '/output', 'GITHUB_STEP_SUMMARY': folder + '/summary'}
            env.update(extra or {})
            with patch('urllib.request.urlopen', respond), patch.object(module.subprocess, 'run', git), patch('sys.stdout', io.StringIO()):
                getattr(module, action)(env)
            output = Path(env['GITHUB_OUTPUT'])
            return output.read_text() if output.exists() else ''

    def test_a_published_nightly_from_main_is_promoted_by_tag_or_digest(self):
        for given in ['sha-' + COMMIT + '-run-123-1', 'sha-' + COMMIT + '-run-123-1-google', NIGHTLY,
                      'ghcr.io/example/messages-server:sha-' + COMMIT + '-run-123-1', ' ghcr.io/example/messages-server@' + NIGHTLY + '\n']:
            with self.subTest(given=given):
                output = self.run_script('check', self.world(), {'INPUT_IMAGE': given})
                self.assertIn('image=ghcr.io/example/messages-server\n', output)
                self.assertIn('digest=' + NIGHTLY + '\n', output)
                self.assertIn('commit=' + COMMIT + '\n', output)
                self.assertIn('tags<<TAGS\nlatest\nlatest-google\n0.1.453\n0.1.453-google\nTAGS\n', output)

    def test_anything_else_is_refused_before_a_tag_moves(self):
        def change(**changes):
            world = self.world()
            for key, value in changes.items():
                if key in ('run', 'jobs', 'versions') and callable(value):
                    value(world[key])
                else:
                    world[key] = value
            return world
        given = 'sha-' + COMMIT + '-run-123-1'
        cases = {
            'a stable tag': (self.world(), 'latest'),
            'nightly by name': (self.world(), 'nightly'),
            'a shell word': (self.world(), given + '; echo'),
            'an unknown digest': (self.world(), 'sha256:' + 'e' * 64),
            'a tag of no image': (self.world(), 'sha-' + 'e' * 40 + '-run-123-1'),
            'an image no build tagged': (change(versions=lambda versions: versions[0]['metadata']['container']['tags'].remove('sha-' + COMMIT + '-run-123-1')), NIGHTLY),
            'two builds on one image': (change(versions=lambda versions: versions[0]['metadata']['container']['tags'].append('sha-' + 'e' * 40 + '-run-7-1')), NIGHTLY),
            'a run that is not there': (change(run=None), given),
            'another workflow': (change(run=lambda run: run.update(path='.github/workflows/other.yml')), given),
            'another branch': (change(run=lambda run: run.update(head_branch='work')), given),
            'another commit': (change(run=lambda run: run.update(head_sha='e' * 40)), given),
            'a pull request': (change(run=lambda run: run.update(event='pull_request')), given),
            'an image job that failed': (change(jobs=[{'name': 'image', 'conclusion': 'failure'}]), given),
            'a commit no longer on main': (change(compare='diverged'), given),
            'a version that is not a tag': (change(project='<Project><Version>1.0</Version></Project>'), given),
            'source not published': (change(source=''), given),
            'a bare manifest': (change(registry={NIGHTLY: (NIGHTLY, 'application/vnd.oci.image.manifest.v1+json')}), given),
            'not in the public registry': (change(registry={}), given),
        }
        for name, (world, image) in cases.items():
            with self.subTest(name):
                with self.assertRaises(SystemExit) as refused:
                    self.run_script('check', world, {'INPUT_IMAGE': image})
                self.assertIn('Refusing to promote', str(refused.exception.code))

    def test_verify_needs_every_stable_tag_on_the_digest(self):
        tags = 'latest\nlatest-google\n0.1.453\n0.1.453-google\n'
        promoted = {tag: (NIGHTLY, INDEX) for tag in tags.split()}
        world = self.world()
        world['registry'] = promoted
        self.run_script('verify', world, {'IMAGE': 'ghcr.io/example/messages-server', 'DIGEST': NIGHTLY, 'TAGS': tags})
        for tag in tags.split():
            with self.subTest(tag=tag):
                world['registry'] = dict(promoted)
                world['registry'][tag] = (STABLE, INDEX)
                with self.assertRaises(SystemExit):
                    self.run_script('verify', world, {'IMAGE': 'ghcr.io/example/messages-server', 'DIGEST': NIGHTLY, 'TAGS': tags})

    def steps(self):
        """The promote workflow's steps, each as its text, in order."""
        text = PROMOTE_WORKFLOW.read_text()
        body = text.split('\n    steps:\n', 1)[1]
        return ['      - ' + step for step in body.split('\n      - ')[1:]]

    # Since the Linux release (2026-11-12): everything that can fail comes before a tag moves, and a dry run moves and publishes nothing.
    def test_workflow_checks_and_packages_before_a_tag_moves_and_publishes_after(self):
        steps = self.steps()
        def at(text):
            found = [i for i, step in enumerate(steps) if text in step]
            self.assertEqual(len(found), 1, text)
            return found[0]
        token = at('publish-linux.sh check')
        package = at('package-linux.sh --image')
        retag = at('docker buildx imagetools create')
        verify = at('promote-server-image.py verify')
        publish = at('publish-linux.sh publish')
        self.assertLess(at('check_install.py PromoteChecks LinuxPackageChecks'), at('promote-server-image.py check'))
        self.assertLess(at('promote-server-image.py check'), token)
        self.assertLess(token, package)
        self.assertLess(package, retag)
        self.assertLess(retag, verify)
        self.assertLess(verify, publish)
        # Every step that needs the release token, logs in, moves a tag or publishes is skipped in a dry run; only a dry run keeps the package.
        for i, step in enumerate(steps):
            moves = any(word in step for word in ['MESSAGES_SOURCE_RELEASE_TOKEN', 'imagetools', 'promote-server-image.py verify',
                                                  'docker/login-action', 'docker/setup-buildx-action', 'publish-linux.sh'])
            with self.subTest(step=i):
                self.assertEqual(moves, "if: ${{ !inputs.dry_run }}" in step, step)
                self.assertEqual('upload-artifact' in step, "if: ${{ inputs.dry_run }}" in step, step)
        # The token reaches gh through the environment alone, and no input or step output is put into a script's text.
        for step in steps:
            if '\n        run: ' in step:
                run = step.split('\n        run: ', 1)[1]
                self.assertNotIn('${{', run, step)
        self.assertNotIn('--clobber', PROMOTE_WORKFLOW.read_text())

    def test_only_a_real_promotion_joins_the_image_workflows_group(self):
        text = PROMOTE_WORKFLOW.read_text()
        group = text.split('\nconcurrency:\n  group: ', 1)[1].split('\n', 1)[0]
        self.assertEqual(group, "${{ inputs.dry_run && format('messages-server-promote-dry-run-{0}', github.run_id) || 'messages-server-ghcr' }}")
        self.assertIn('\n      dry_run:\n', text)
        self.assertIn('\n        default: false\n        type: boolean\n', text)


PROMOTE_WORKFLOW = ROOT / '.github/workflows/messages-server-promote.yml'
PACKAGE_SCRIPT = ROOT / 'docker/messages-server/release/package-linux.sh'
PUBLISH_SCRIPT = ROOT / 'docker/messages-server/release/publish-linux.sh'
INSTALL_FILES = ['install.sh', 'uninstall.sh', 'update.sh', 'messages-server.service', 'messages-server-update.service',
                 'messages-server-update.timer', 'google.conf', 'README.md']
EXECUTABLE = {'messages-server', 'mlink', 'messenger-helper', 'instagram-helper', 'gmessages-helper', 'gvoice-helper',
              'messages-tailnet', 'install.sh', 'uninstall.sh', 'update.sh', 'createdump'}
PACKAGE_IMAGE = 'ghcr.io/example/messages-server@' + NIGHTLY
COMMIT_TIME = 1762934400
LONG_MODULE = 'github.com/example-organization/a-rather-long-module-name-for-the-door/v2@v2.13.0-20261101000000-0123456789ab'

# The package script's Docker: pull, create, the revision label, cp of /app from a stand-in tree, rm.
PACKAGE_DOCKER = r'''#!/usr/bin/env python3
import json, os, shutil, sys
a = sys.argv[1:]
with open(os.environ['CALLS'], 'a') as log:
    log.write(json.dumps(a) + '\n')
if a[0] == 'pull':
    sys.exit(0)
if a[0] == 'create':
    print('synthetic-container')
elif a[0] == 'container' and a[1] == 'inspect':
    print(os.environ.get('IMAGE_REVISION', ''))
elif a[0] == 'cp':
    assert a[1] == 'synthetic-container:/app/.', a
    shutil.copytree(os.environ['IMAGE_APP'], a[2], symlinks=True, dirs_exist_ok=True)
elif a[0] == 'rm':
    pass
else:
    sys.exit('unexpected docker ' + ' '.join(a))
'''

# The publish script's gh, over a JSON file standing in for the public repository's releases.
PUBLISH_GH = r'''#!/usr/bin/env python3
import hashlib, json, os, sys
a = sys.argv[1:]
with open(os.environ['CALLS'], 'a') as log:
    log.write(json.dumps(a) + '\n')
path = os.environ['RELEASES']
state = json.load(open(path))
def save():
    json.dump(state, open(path, 'w'))
def fail(text):
    sys.stderr.write('gh: ' + text + '\n')
    sys.exit(1)
repo = 'repos/example/source'
if a[0] == 'api':
    method = a[a.index('-X') + 1] if '-X' in a else 'GET'
    target = [word for word in a[1:] if word.startswith('repos/')][0]
    if state.get('auth') == 'expired':
        fail('Bad credentials (HTTP 401)')
    if target == repo and method == 'GET':
        print('example/source')
    elif target == repo + '/releases' and method == 'POST':
        if not state.get('write', True):
            fail('Resource not accessible by personal access token (HTTP 403)')
        assert 'draft=true' in a, a
        state['drafts'] = state.get('drafts', 0) + 1
        save()
        print(77)
    elif target == repo + '/releases/77' and method == 'DELETE':
        state['drafts'] -= 1
        save()
    elif target.startswith(repo + '/releases/tags/'):
        release = state['releases'].get(target.rsplit('/', 1)[1])
        if release is None:
            fail('Not Found (HTTP 404)')
        for name, digest in release['assets'].items():
            print(name + ' ' + digest)
    elif target == repo + '/releases/latest':
        print(state.get('latest') or '')
    else:
        fail('unexpected ' + method + ' ' + target)
elif a[0] == 'release':
    tag = a[2]
    assert a[a.index('--repo') + 1] == 'example/source', a
    if a[1] == 'create':
        assert tag in state['tags'] and tag not in state['releases'] and '--verify-tag' in a and '--latest=false' in a, a
        state['releases'][tag] = {'assets': {}, 'title': a[a.index('--title') + 1], 'notes': a[a.index('--notes') + 1]}
    elif a[1] == 'upload':
        assert '--clobber' not in a, a
        for file in a[5:]:
            name = os.path.basename(file)
            if name in state['releases'][tag]['assets']:
                fail('asset under the same name already exists: ' + name)
            if state.get('upload_fails_at') == name:
                save()
                fail('upload failed (HTTP 502)')
            state['releases'][tag]['assets'][name] = 'sha256:' + hashlib.sha256(open(file, 'rb').read()).hexdigest()
            save()
    elif a[1] == 'edit':
        assert a[5:] == ['--latest'], a
        state['latest'] = tag
    else:
        fail('unexpected release ' + a[1])
    save()
else:
    fail('unexpected ' + a[0])
'''


class LinuxPackageChecks(unittest.TestCase):
    """docker/messages-server/release: the Linux package made from an image's /app, and its release. Docker and gh are stand-ins."""

    def fake_app(self, folder):
        app = folder / 'app'
        files = {
            'messages-server': 0o700, 'mlink': 0o755, 'messenger-helper': 0o755, 'instagram-helper': 0o755, 'gmessages-helper': 0o755,
            'gvoice-helper': 0o755, 'messages-tailnet': 0o755, 'createdump': 0o755, 'libSkiaSharp.so': 0o755, 'messages-server.dll': 0o600,
            'Zeta.dll': 0o664, 'alpha.dll': 0o644, 'messages-server.deps.json': 0o644,
            'licenses/THIRD_PARTY_NOTICES.md': 0o644, 'licenses/SMBLibrary/NOTICE.txt': 0o644, 'licenses/tailnet/go/LICENSE': 0o644,
            'licenses/tailnet/' + LONG_MODULE + '/LICENSE': 0o444,
            'install/assets/app-icon.png': 0o644, 'install/docker/messages-server/chrome-seccomp.json': 0o644,
        }
        for name, mode in files.items():
            target = app / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(('synthetic ' + name + '\n').encode())
            target.chmod(mode)
        (app / 'licenses').chmod(0o700)
        return app, [name for name in files if not name.startswith('install/')]

    def fake_source(self, folder):
        source = folder / 'source'
        for relative in ['docker/messages-server/systemd/' + name for name in INSTALL_FILES] + \
                        ['docker/messages-server/chrome-policy.json', 'docker/messages-server/proxmox/install-proxmox.sh']:
            target = source / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            text = (ROOT / relative).read_bytes().replace(b'\r\n', b'\n')
            # A Windows checkout: CRLF endings and a byte order mark, which the package must not carry.
            target.write_bytes(b'\xef\xbb\xbf' + text.replace(b'\n', b'\r\n'))
        project = source / 'src/NativeBeeper.App/NativeBeeper.App.csproj'
        project.parent.mkdir(parents=True)
        project.write_text('<Project>\r\n  <PropertyGroup>\r\n    <Version>0.1.453</Version>\r\n  </PropertyGroup>\r\n</Project>\r\n')
        return source

    def package(self, folder, app, source, out, image=PACKAGE_IMAGE, revision=COMMIT, extra_env=None):
        bin_dir = folder / 'bin'
        bin_dir.mkdir(exist_ok=True)
        (bin_dir / 'docker').write_text(PACKAGE_DOCKER)
        (bin_dir / 'docker').chmod(0o755)
        env = {'PATH': str(bin_dir) + ':/usr/local/bin:/usr/bin:/bin', 'CALLS': str(folder / 'calls'), 'IMAGE_APP': str(app),
               'IMAGE_REVISION': revision, 'GITHUB_OUTPUT': str(folder / 'output'), 'TMPDIR': str(folder)}
        env.update(extra_env or {})
        return subprocess.run(['bash', str(PACKAGE_SCRIPT), '--image', image, '--commit', COMMIT, '--time', str(COMMIT_TIME),
                               '--source', str(source), '--out', str(out)], env=env, capture_output=True, text=True)

    def test_the_package_is_the_images_app_with_the_install_files_laid_out_as_packages_are(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            app, programs = self.fake_app(folder)
            source = self.fake_source(folder)
            out = folder / 'out'
            result = self.package(folder, app, source, out)
            self.assertEqual(result.returncode, 0, result.stderr)
            name = 'messages-server-0.1.453-linux-x64'
            self.assertEqual(sorted(path.name for path in out.iterdir()),
                             sorted([name + '.tar.gz', name + '.tar.gz.sha256', 'messages-server-linux-x64.tar.gz',
                                     'messages-server-linux-x64.tar.gz.sha256', 'install-proxmox.sh']))
            archive = out / (name + '.tar.gz')
            data = archive.read_bytes()
            self.assertEqual(data[:3], b'\x1f\x8b\x08')
            self.assertEqual(data[4:8], b'\0\0\0\0', 'gzip carries a timestamp')
            self.assertFalse(data[3] & 0x08, 'gzip carries a file name')
            import hashlib
            digest = hashlib.sha256(data).hexdigest()
            self.assertEqual((out / (name + '.tar.gz.sha256')).read_text(), digest + '  ' + name + '.tar.gz\n')
            self.assertEqual((out / 'messages-server-linux-x64.tar.gz').read_bytes(), data)
            self.assertEqual((out / 'messages-server-linux-x64.tar.gz.sha256').read_text(), digest + '  messages-server-linux-x64.tar.gz\n')
            self.assertEqual((out / 'install-proxmox.sh').read_bytes(),
                             (ROOT / 'docker/messages-server/proxmox/install-proxmox.sh').read_bytes().replace(b'\r\n', b'\n'))
            self.assertIn('version=0.1.453\npackage=' + name + '\nsha256=' + digest + '\n', (folder / 'output').read_text())
            # Pulled and created as linux/amd64 by digest, and the container removed with its volumes.
            calls = [json.loads(line) for line in (folder / 'calls').read_text().splitlines()]
            self.assertEqual(calls[0], ['pull', '--quiet', '--platform', 'linux/amd64', PACKAGE_IMAGE])
            self.assertEqual(calls[1], ['create', '--platform', 'linux/amd64', PACKAGE_IMAGE])
            self.assertEqual(calls[-1], ['rm', '-f', '-v', 'synthetic-container'])

            with tarfile.open(archive, 'r:gz') as tar:
                members = tar.getmembers()
                contents = {member.name: tar.extractfile(member).read() for member in members if member.isfile()}
            names = [member.name for member in members]
            self.assertEqual(names[0], name)
            self.assertEqual(names, sorted(names, key=str.encode))
            folders = {name + '/' + '/'.join(parts[:i]) for parts in (program.split('/') for program in programs + INSTALL_FILES)
                       for i in range(1, len(parts))}
            expected = {name} | folders | {name + '/' + file for file in programs + INSTALL_FILES + ['chrome-policy.json']}
            self.assertEqual(set(names), expected)
            self.assertNotIn(name + '/install', names)
            self.assertTrue(any(len(entry) > 100 for entry in names))
            for member in members:
                with self.subTest(member=member.name):
                    relative = member.name[len(name) + 1:]
                    self.assertTrue(member.isdir() or member.isfile())
                    self.assertEqual((member.uid, member.gid, member.uname, member.gname), (0, 0, 'root', 'root'))
                    self.assertEqual(member.mtime, COMMIT_TIME)
                    self.assertEqual(member.mode, 0o755 if member.isdir() or relative in EXECUTABLE else 0o644)
                    self.assertNotIn('atime', member.pax_headers)
                    self.assertNotIn('ctime', member.pax_headers)
            for file in INSTALL_FILES + ['chrome-policy.json']:
                with self.subTest(file=file):
                    body = contents[name + '/' + file]
                    self.assertNotIn(b'\r', body)
                    self.assertFalse(body.startswith(b'\xef\xbb\xbf'))
            self.assertEqual(contents[name + '/messages-server'], b'synthetic messages-server\n')
            # install-proxmox.sh's own test of a package.
            self.assertRegex(name, r'^messages-server-[0-9A-Za-z.+-]+-linux-x64$')
            for member in ['install.sh', 'messages-server', 'messages-tailnet', 'gmessages-helper']:
                self.assertIn(name + '/' + member, contents)

            # The same image and commit give the same bytes, whatever the files' times and modes on the runner.
            os.utime(app / 'mlink', (1, 1))
            (app / 'alpha.dll').chmod(0o600)
            again = folder / 'again'
            result = self.package(folder, app, source, again)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual((again / (name + '.tar.gz')).read_bytes(), data)

    def test_anything_unexpected_stops_the_package(self):
        cases = {
            'an image named by tag': dict(image='ghcr.io/example/messages-server:latest'),
            'an image from another commit': dict(revision='e' * 40),
            'an image without its revision': dict(revision=''),
            'an app without the tailnet door': dict(remove='messages-tailnet'),
            'an app without mlink': dict(remove='mlink'),
            'a link in the app': dict(link='helper-link'),
            'an app that already holds install.sh': dict(add='install.sh'),
            'a checkout without the update': dict(source_remove='docker/messages-server/systemd/update.sh'),
            'a checkout without install-proxmox.sh': dict(source_remove='docker/messages-server/proxmox/install-proxmox.sh'),
            'a version that is not three numbers': dict(version='1.0'),
            'an output folder in use': dict(used=True),
        }
        for label, case in cases.items():
            with self.subTest(label), tempfile.TemporaryDirectory() as temp:
                folder = Path(temp)
                app, _ = self.fake_app(folder)
                source = self.fake_source(folder)
                if 'remove' in case:
                    (app / case['remove']).unlink()
                if 'link' in case:
                    (app / case['link']).symlink_to('messages-server')
                if 'add' in case:
                    (app / case['add']).write_text('from the image\n')
                if 'source_remove' in case:
                    (source / case['source_remove']).unlink()
                if 'version' in case:
                    (source / 'src/NativeBeeper.App/NativeBeeper.App.csproj').write_text('<Project><Version>' + case['version'] + '</Version></Project>')
                out = folder / 'out'
                if case.get('used'):
                    out.mkdir()
                    (out / 'old.tar.gz').write_text('old')
                result = self.package(folder, app, source, out, image=case.get('image', PACKAGE_IMAGE), revision=case.get('revision', COMMIT))
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn('package-linux: ', result.stderr)
                published = [path.name for path in out.iterdir()] if out.exists() else []
                self.assertFalse([name for name in published if name.endswith('.sha256')], published)

    def built_assets(self, folder, version='0.1.453', body=b'package'):
        assets = folder / 'assets'
        assets.mkdir()
        import hashlib
        for name in ['messages-server-' + version + '-linux-x64.tar.gz', 'messages-server-linux-x64.tar.gz']:
            (assets / name).write_bytes(body)
            (assets / (name + '.sha256')).write_text(hashlib.sha256(body).hexdigest() + '  ' + name + '\n')
        (assets / 'install-proxmox.sh').write_text('#!/usr/bin/env bash\n')
        return assets

    def gh(self, folder, state, arguments, token='synthetic'):
        bin_dir = folder / 'bin'
        bin_dir.mkdir(exist_ok=True)
        (bin_dir / 'gh').write_text(PUBLISH_GH)
        (bin_dir / 'gh').chmod(0o755)
        (folder / 'releases.json').write_text(json.dumps(state))
        calls = folder / 'calls'
        if calls.exists():
            calls.unlink()
        env = {'PATH': str(bin_dir) + ':/usr/local/bin:/usr/bin:/bin', 'CALLS': str(calls), 'RELEASES': str(folder / 'releases.json'),
               'GH_TOKEN': token, 'SOURCE_REPOSITORY': 'example/source', 'GITHUB_RUN_ID': '555', 'TMPDIR': str(folder)}
        result = subprocess.run(['bash', str(PUBLISH_SCRIPT)] + arguments, env=env, capture_output=True, text=True)
        after = json.loads((folder / 'releases.json').read_text())
        logged = [json.loads(line) for line in calls.read_text().splitlines()] if calls.exists() else []
        return result, after, logged

    def publish_arguments(self, assets, tag='src-' + COMMIT[:12]):
        return ['publish', '--tag', tag, '--version', '0.1.453', '--image', PACKAGE_IMAGE, '--assets', str(assets)]

    def test_the_token_is_checked_by_a_draft_made_and_removed_before_anything_moves(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            result, after, calls = self.gh(folder, {'releases': {}, 'tags': []}, ['check'])
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(after.get('drafts'), 0)
            post = next(call for call in calls if 'POST' in call)
            self.assertIn('tag_name=release-write-check-555', post)
            self.assertNotIn('synthetic', json.dumps(calls) + result.stdout + result.stderr)
            for label, state, token in [('missing', {'releases': {}, 'tags': []}, ''),
                                        ('expired', {'releases': {}, 'tags': [], 'auth': 'expired'}, 'synthetic'),
                                        ('read only', {'releases': {}, 'tags': [], 'write': False}, 'synthetic')]:
                with self.subTest(label):
                    result, after, calls = self.gh(folder, state, ['check'], token)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn('MESSAGES_SOURCE_RELEASE_TOKEN', result.stderr)
                    self.assertIn('docs/release.md', result.stderr)
                    self.assertFalse(after.get('drafts'))
                    if not token:
                        self.assertEqual(calls, [])

    def test_a_first_promotion_makes_the_release_and_marks_it_latest_last(self):
        tag = 'src-' + COMMIT[:12]
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            assets = self.built_assets(folder)
            result, after, calls = self.gh(folder, {'releases': {'src-older': {'assets': {'x': 'sha256:1'}}}, 'tags': [tag], 'latest': 'src-older'},
                                          self.publish_arguments(assets))
            self.assertEqual(result.returncode, 0, result.stderr)
            release = after['releases'][tag]
            self.assertEqual(release['title'], 'Message Link Server 0.1.453')
            self.assertIn(PACKAGE_IMAGE, release['notes'])
            self.assertLessEqual(len(release['notes'].splitlines()), 2)
            self.assertEqual(set(release['assets']), {'messages-server-0.1.453-linux-x64.tar.gz', 'messages-server-0.1.453-linux-x64.tar.gz.sha256',
                                                      'messages-server-linux-x64.tar.gz', 'messages-server-linux-x64.tar.gz.sha256', 'install-proxmox.sh'})
            self.assertEqual(after['latest'], tag)
            kinds = [call[1] if call[0] == 'release' else 'api' for call in calls]
            self.assertLess(kinds.index('create'), kinds.index('upload'))
            self.assertLess(kinds.index('upload'), kinds.index('edit'))
            upload = next(call for call in calls if call[:2] == ['release', 'upload'])
            files = [os.path.basename(file) for file in upload[5:]]
            self.assertEqual([file.endswith('.sha256') for file in files], sorted(file.endswith('.sha256') for file in files))

    def test_a_release_promoted_again_keeps_its_files_and_a_stopped_one_is_finished(self):
        tag = 'src-' + COMMIT[:12]
        import hashlib
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            assets = self.built_assets(folder)
            names = sorted(path.name for path in assets.iterdir())
            def digest(name):
                return 'sha256:' + hashlib.sha256((assets / name).read_bytes()).hexdigest()
            # Promoted again or rolled back to: nothing is added or replaced, it only becomes latest.
            old = {name: 'sha256:' + '0' * 64 for name in names}
            result, after, calls = self.gh(folder, {'releases': {tag: {'assets': old}}, 'tags': [tag], 'latest': 'src-newer'}, self.publish_arguments(assets))
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(after['releases'][tag]['assets'], old)
            self.assertEqual(after['latest'], tag)
            self.assertFalse([call for call in calls if call[:2] in (['release', 'upload'], ['release', 'create'])])
            # A run stopped during its upload: the run again adds what is missing, then marks it latest.
            state = {'releases': {}, 'tags': [tag], 'latest': 'src-older', 'upload_fails_at': 'messages-server-linux-x64.tar.gz'}
            result, after, _ = self.gh(folder, state, self.publish_arguments(assets))
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(after['latest'], 'src-older')
            after.pop('upload_fails_at')
            result, after, calls = self.gh(folder, after, self.publish_arguments(assets))
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(after['releases'][tag]['assets'], {name: digest(name) for name in names})
            self.assertEqual(after['latest'], tag)
            # A file an earlier attempt left with other bytes stops the run: no upload, not latest.
            partial = {'messages-server-linux-x64.tar.gz': 'sha256:' + '1' * 64}
            result, after, calls = self.gh(folder, {'releases': {tag: {'assets': partial}}, 'tags': [tag], 'latest': 'src-older'},
                                          self.publish_arguments(assets))
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('different messages-server-linux-x64.tar.gz', result.stderr)
            self.assertEqual(after['releases'][tag]['assets'], partial)
            self.assertEqual(after['latest'], 'src-older')

    def test_publish_refuses_what_is_not_a_package(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            assets = self.built_assets(folder)
            for label, arguments in [('a tag that is not src-', self.publish_arguments(assets, tag='v0.1.453')),
                                     ('a missing file', self.publish_arguments(folder / 'nowhere'))]:
                with self.subTest(label):
                    result, after, calls = self.gh(folder, {'releases': {}, 'tags': ['v0.1.453']}, arguments)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertEqual(after['releases'], {})
            (assets / 'install-proxmox.sh').unlink()
            result, after, calls = self.gh(folder, {'releases': {}, 'tags': ['src-' + COMMIT[:12]]}, self.publish_arguments(assets))
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(calls, [])


if __name__ == '__main__':
    unittest.main(verbosity=2)
