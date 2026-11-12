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


if __name__ == '__main__':
    unittest.main(verbosity=2)
