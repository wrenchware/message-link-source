"""Offline checks of proxmox/install-proxmox.sh. pct, pveam, pvesm, pvesh, pveversion, whiptail and curl are stand-ins;
the container is a folder in a temporary directory and nothing reaches a Proxmox host or the network."""
import fcntl
import hashlib
import io
import json
import os
from pathlib import Path
import pty
import subprocess
import tarfile
import tempfile
import termios
import unittest


ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / 'docker/messages-server/proxmox/install-proxmox.sh'
RELEASE = 'https://github.com/wrenchware/message-link-source/releases/latest/download/messages-server-linux-x64.tar.gz'
VERSION = '10.20.1'
CHECK, CROSS = chr(0x2713), chr(0x2717)

# One stand-in for every Proxmox command, told apart by its name. Each call is logged as JSON; the container's files
# live under $CT_ROOT/<id>.
STAND_IN = r'''#!/usr/bin/env python3
import json, os, pathlib, shutil, sys
name = pathlib.Path(sys.argv[0]).name
a = sys.argv[1:]
state = pathlib.Path(os.environ['STATE'])
with open(state / 'calls', 'a') as log:
    log.write(json.dumps([name] + a) + '\n')
existing = os.environ.get('EXISTING', '')
made = state / 'made'
def exists(ct):
    return ct == existing or (made.exists() and made.read_text() == ct)
def ct_path(ct, path):
    return pathlib.Path(os.environ['CT_ROOT']) / ct / path.lstrip('/')

if name == 'pct':
    verb = a[0]
    if verb == 'list':
        print('VMID       Status     Lock         Name')
        print('100        running                 other')
        if existing:
            print(f'{existing}        running                 messages-server')
    elif verb == 'config':
        if a[1] == existing:
            print('arch: amd64')
            print(f'description: {os.environ.get("EXISTING_NOTES", "")}')
            print('tags: messages-server')
        elif a[1] == '100':
            print('tags: other')
        else:
            sys.exit(2)
    elif verb == 'status':
        if not exists(a[1]) and a[1] != '100':
            print(f"Configuration file 'nodes/pve/lxc/{a[1]}.conf' does not exist", file=sys.stderr)
            sys.exit(2)
        print('status: ' + os.environ.get('EXISTING_STATUS', 'running'))
    elif verb == 'create':
        made.write_text(a[1])
        print('extracting archive')
    elif verb == 'push':
        target = ct_path(a[1], a[3])
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(a[2], target)
        if '--perms' in a:
            target.chmod(int(a[a.index('--perms') + 1], 8))
    elif verb == 'pull':
        source = ct_path(a[1], a[2])
        if not source.exists():
            sys.exit(1)
        shutil.copyfile(source, a[3])
    elif verb == 'set':
        (state / 'notes').write_text(a[a.index('--description') + 1])
    elif verb == 'exec':
        command = ' '.join(a[a.index('--') + 1:])
        if 'hostname -I' in command:
            print('192.0.2.50 fd00::50')
        elif 'journalctl' in command and 'grep -q' in command:
            pass
        elif 'journalctl' in command:
            print('server listening transport=network')
            print('server setup code=ABCD-EFGH')
            print('server pairing code=123456')
            print('server tls fingerprint sha256=' + 'ab' * 32)
        elif command.startswith('test -x'):
            sys.exit(0 if os.environ.get('INSTALLED', '1') == '1' else 1)
        elif 'install.sh' in command:
            print('Installing Google Chrome from Google\'s repository...')
elif name == 'pvesh':
    print('105')
elif name == 'pvesm':
    print('Name      Type   Status   Total   Used   Available   %')
    if a[a.index('-content') + 1] == 'vztmpl':
        print('local     dir    active   100     1      99          1.00%')
    else:
        print('local-lvm lvmthin active 100 1 99 1.00%')
        print('tank      zfspool active 100 1 99 1.00%')
elif name == 'pveam':
    if a[0] == 'list':
        print('NAME                                                         SIZE')
    elif a[0] == 'available':
        print('system          debian-12-standard_12.7-1_amd64.tar.zst')
        print('system          debian-13-standard_13.1-2_amd64.tar.zst')
    elif a[0] == 'update':
        print('update successful')
elif name == 'ip':
    print('4: vmbr0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc noqueue state UP mode DEFAULT')
elif name == 'id':
    print('0')
elif name == 'uname':
    print('x86_64')
elif name == 'curl':
    url = a[-1]
    out = a[a.index('-o') + 1]
    served = {os.environ['SERVED']: os.environ['PACKAGE'], os.environ['SERVED'] + '.sha256': os.environ['PACKAGE'] + '.sha256'}
    if url not in served:
        sys.exit(22)
    shutil.copyfile(served[url], out)
elif name == 'whiptail':
    # Takes the shown default, or the call named in WHIPTAIL_CANCEL (1-based) returns Cancel. Answers go to stderr, as
    # whiptail's do. WHIPTAIL_ANSWERS gives answers by title.
    count = len([line for line in (state / 'calls').read_text().splitlines() if json.loads(line)[0] == 'whiptail'])
    if str(count) == os.environ.get('WHIPTAIL_CANCEL', ''):
        sys.exit(1)
    answers = json.loads(os.environ.get('WHIPTAIL_ANSWERS', '{}'))
    title = a[a.index('--title') + 1] if '--title' in a else ''
    if '--yesno' in a or '--msgbox' in a:
        sys.exit(answers.get(title, 0))
    if title in answers:
        sys.stderr.write(answers[title].replace('{root}', os.environ['TEST_ROOT']))
    elif '--inputbox' in a:
        sys.stderr.write(a[a.index('--inputbox') + 4] if len(a) > a.index('--inputbox') + 4 else '')
    elif '--menu' in a:
        sys.stderr.write(a[a.index('--default-item') + 1])
'''

COMMANDS = ['pct', 'pveam', 'pvesm', 'pvesh', 'pveversion', 'whiptail', 'curl', 'ip', 'id', 'uname', 'chown']


def package(folder, google=True):
    """A package with the members the script checks, and its .sha256."""
    path = Path(folder) / f'messages-server-{VERSION}-linux-x64.tar.gz'
    top = f'messages-server-{VERSION}-linux-x64'
    members = ['install.sh', 'messages-server', 'messages-tailnet'] + (['gmessages-helper'] if google else [])
    with tarfile.open(path, 'w:gz') as archive:
        for member in [''] + members:
            info = tarfile.TarInfo(f'{top}/{member}'.rstrip('/'))
            if member:
                info.size = 7
                archive.addfile(info, io.BytesIO(b'fixture'))
            else:
                info.type = tarfile.DIRTYPE
                archive.addfile(info)
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    Path(str(path) + '.sha256').write_text(f'{digest}  {path.name}\n')
    return path, digest


class ProxmoxChecks(unittest.TestCase):
    def run_script(self, *options, whiptail=False, terminal=False, extra=None, seed=None):
        folder = tempfile.TemporaryDirectory(prefix='messages-proxmox-check-')
        self.addCleanup(folder.cleanup)
        root = Path(folder.name)
        commands = root / 'commands'
        commands.mkdir()
        for name in COMMANDS:
            if name == 'whiptail' and not whiptail:
                continue
            target = commands / name
            target.write_text(STAND_IN if name != 'chown' else '#!/bin/sh\nexit 0\n')
            target.chmod(0o755)
        state = root / 'state'
        state.mkdir()
        (state / 'calls').write_text('')
        (root / 'pve').mkdir()
        (root / 'subuid').write_text('root:100000:65536\n')
        script = SCRIPT.read_text().replace('/etc/pve', str(root / 'pve')).replace('/etc/subuid', str(root / 'subuid'))
        (root / 'install-proxmox.sh').write_text(script)
        built, digest = package(root)
        for path, content in (seed or {}).items():
            target = root / 'ct' / path.lstrip('/')
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content)
        env = {
            'PATH': f'{commands}:/usr/bin:/bin', 'STATE': str(state), 'CT_ROOT': str(root / 'ct'),
            'SERVED': RELEASE, 'PACKAGE': str(built), 'TERM': 'xterm', 'HOME': str(root), 'TEST_ROOT': str(root),
        }
        env.update(extra or {})
        arguments = ['bash', str(root / 'install-proxmox.sh'), *options]
        if terminal:
            # A terminal of its own, so /dev/tty opens as it does in the Proxmox shell; stdout stays a pipe.
            leader, follower = pty.openpty()

            def take_terminal():
                os.setsid()
                fcntl.ioctl(0, termios.TIOCSCTTY, 0)
            try:
                result = subprocess.run(arguments, env=env, stdin=follower, capture_output=True, text=True, timeout=30,
                                        preexec_fn=take_terminal)
            finally:
                os.close(follower)
                os.close(leader)
        else:
            result = subprocess.run(arguments, env=env, stdin=subprocess.DEVNULL, capture_output=True, text=True,
                                    timeout=30, start_new_session=True)
        calls = [json.loads(line) for line in (state / 'calls').read_text().splitlines()]
        notes = (state / 'notes').read_text() if (state / 'notes').exists() else None
        return result, calls, notes, root, built, digest

    def created(self, calls):
        return [call for call in calls if call[:2] == ['pct', 'create']]

    def test_yes_downloads_the_release_and_makes_the_container(self):
        result, calls, notes, root, _, digest = self.run_script('--yes')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        downloads = [call[-1] for call in calls if call[0] == 'curl']
        self.assertEqual(downloads, [RELEASE, RELEASE + '.sha256'])
        [create] = self.created(calls)
        self.assertEqual(create[2], '105')
        self.assertEqual(create[3], 'local:vztmpl/debian-13-standard_13.1-2_amd64.tar.zst')
        options = create[4:]
        for name, value in [('--unprivileged', '1'), ('--features', 'nesting=1'), ('--rootfs', 'local-lvm:8'),
                            ('--memory', '4096'), ('--cores', '2'), ('--net0', 'name=eth0,bridge=vmbr0,ip=dhcp'),
                            ('--onboot', '1'), ('--tags', 'messages-server'), ('--hostname', 'messages-server')]:
            self.assertEqual(options[options.index(name) + 1], value, name)
        self.assertNotIn('--mp0', options)
        self.assertIn(['pveam', 'download', 'local', 'debian-13-standard_13.1-2_amd64.tar.zst'], calls)
        ct = root / 'ct/105'
        environment = (ct / 'etc/messages-server/environment').read_text()
        self.assertIn('MESSAGES_HOSTNAMES=messages-server,192.0.2.50\n', environment)
        self.assertIn('MESSAGES_TAILNET=1\n', environment)
        self.assertNotIn('MESSAGES_ADMIN_PASSWORD', environment)
        self.assertIn(f'MESSAGES_RELEASE_URL={RELEASE}\n', (ct / 'etc/messages-server/update').read_text())
        self.assertIn('MESSAGES_NETWORK_ISOLATED=1', (ct / 'etc/systemd/system/messages-server.service.d/proxmox.conf').read_text())
        command = ct / 'usr/local/bin/update'
        self.assertIn('systemctl start messages-server-update', command.read_text())
        self.assertEqual(command.stat().st_mode & 0o777, 0o755)
        self.assertTrue((ct / 'root/messages-server.tar.gz').exists())
        install = [call for call in calls if call[:2] == ['pct', 'exec'] and 'install.sh --google' in ' '.join(call)]
        self.assertEqual(len(install), 1)
        self.assertIn(digest, ' '.join(install[0]))
        # The notes come last, once the address is known.
        self.assertEqual(calls[-1][:3], ['pct', 'set', '105'])
        self.assertIn('## Message Link Server', notes)
        self.assertIn('[https://192.0.2.50:8787/](https://192.0.2.50:8787/)', notes)
        self.assertIn('https://wrenchware.github.io/message-link/proxmox/', notes)
        self.assertIn('`pct exec 105 -- update`', notes)
        self.assertIn('`pct exec 105 -- journalctl -u messages-server -f`', notes)
        out = result.stdout
        self.assertIn(f'{CHECK} Message Link Server {VERSION}, checked against its .sha256', out)
        self.assertIn(f'{CHECK} Made container 105', out)
        self.assertIn('Its page:      https://192.0.2.50:8787/', out)
        self.assertIn('Setup code:    ABCD-EFGH', out)
        self.assertIn('Pairing code:  123456', out)
        self.assertIn('to update now: pct exec 105 -- update', out)
        self.assertNotIn('\033[', out)
        # apt's and pveam's own output stays in the log.
        self.assertNotIn('update successful', out)
        self.assertNotIn('extracting archive', out)

    def test_a_tarball_file_is_used_instead_of_the_release(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        given, _ = package(folder.name)
        result, calls, *_ = self.run_script('--yes', '--no-google', '--tarball', str(given))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse([call for call in calls if call[0] == 'curl'])
        [create] = self.created(calls)
        self.assertEqual(create[create.index('--memory') + 1], '2048')
        self.assertFalse([call for call in calls if 'install.sh --google' in ' '.join(call)])
        # A test package stays: the nightly update would put the stable release over it.
        root = _[1]
        self.assertIn('MESSAGES_RELEASE_URL=\n', (root / 'ct/105/etc/messages-server/update').read_text())
        self.assertIn('keeps the package given with --tarball', result.stdout)

    def test_rerun_with_a_tarball_stops_following_stable_and_without_one_follows_again(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        given, _ = package(folder.name)
        stable = f'# The nightly update.\nMESSAGES_RELEASE_URL={RELEASE}\n'
        result, calls, notes, root, *_ = self.run_script(
            '--yes', '--tarball', str(given), extra={'EXISTING': '112', 'EXISTING_NOTES': ''},
            seed={'112/etc/messages-server/update': stable})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('MESSAGES_RELEASE_URL=\n', (root / 'ct/112/etc/messages-server/update').read_text())
        self.assertIn('Run this script again without --tarball', result.stdout)
        result, calls, notes, root, *_ = self.run_script(
            '--yes', extra={'EXISTING': '112', 'EXISTING_NOTES': ''},
            seed={'112/etc/messages-server/update': '# The nightly update.\nMESSAGES_RELEASE_URL=\n'})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(f'MESSAGES_RELEASE_URL={RELEASE}\n', (root / 'ct/112/etc/messages-server/update').read_text())

    def test_a_mismatched_package_changes_nothing(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        given, _ = package(folder.name)
        Path(str(given) + '.sha256').write_text('0' * 64 + '\n')
        result, calls, *_ = self.run_script('--yes', '--tarball', str(given))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('does not match its .sha256', result.stderr)
        self.assertIn(CROSS, result.stdout)
        self.assertFalse([call for call in calls if call[0] == 'pct' and call[1] != 'list'])

    def test_rerun_updates_the_tagged_container_and_fills_an_empty_address(self):
        result, calls, notes, root, *_ = self.run_script(
            '--yes', extra={'EXISTING': '112', 'EXISTING_NOTES': 'Message Link Server (install-proxmox.sh). Its settings: ...'},
            seed={'112/etc/messages-server/update': '# The nightly update.\nMESSAGES_RELEASE_URL=\n'})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(self.created(calls))
        self.assertIn(['pct', 'config', '112'], calls)
        ct = root / 'ct/112'
        self.assertIn(f'MESSAGES_RELEASE_URL={RELEASE}\n', (ct / 'etc/messages-server/update').read_text())
        self.assertTrue((ct / 'usr/local/bin/update').exists())
        install = [call for call in calls if call[:3] == ['pct', 'exec', '112'] and 'install.sh' in ' '.join(call)]
        self.assertEqual(len(install), 1)
        self.assertNotIn('--google', ' '.join(install[0]))
        self.assertIn('`pct exec 112 -- update`', notes)
        self.assertIn(f'Message Link Server in container 112 is now {VERSION}.', result.stdout)

    def test_rerun_keeps_an_address_and_notes_of_the_owners_own(self):
        given = 'MESSAGES_RELEASE_URL=https://example.invalid/messages-server-linux-x64.tar.gz\n'
        result, calls, notes, root, *_ = self.run_script(
            '--yes', extra={'EXISTING': '112', 'EXISTING_NOTES': 'My own notes'},
            seed={'112/etc/messages-server/update': given})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual((root / 'ct/112/etc/messages-server/update').read_text(), given)
        self.assertIsNone(notes)

    def test_a_container_without_the_tag_is_refused(self):
        result, calls, *_ = self.run_script('--yes', '--ctid', '100')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('not one this script made', result.stderr)
        self.assertFalse(self.created(calls))

    def whiptail_calls(self, calls):
        return [call[1:] for call in calls if call[0] == 'whiptail']

    def test_whiptail_default_settings_ask_once_then_make_it(self):
        result, calls, notes, root, *_ = self.run_script(whiptail=True, terminal=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        screens = self.whiptail_calls(calls)
        self.assertEqual(len(screens), 2, screens)
        self.assertIn('--menu', screens[0])
        self.assertIn('Default', screens[0])
        self.assertIn('Advanced', screens[0])
        confirm = screens[1]
        self.assertIn('--yesno', confirm)
        text = confirm[confirm.index('--yesno') + 1]
        for expected in ['Container 105, messages-server', '8 GB on local-lvm', 'template on local', '4096 MB, 2 cores',
                         'DHCP on vmbr0', 'Google Messages and Google Voice: on', 'Tailscale: on',
                         'Backups: inside the container', 'set on the page with the setup code', 'Nightly update: on']:
            self.assertIn(expected, text)
        self.assertEqual(len(self.created(calls)), 1)
        self.assertIsNotNone(notes)

    def test_whiptail_cancel_changes_nothing(self):
        for cancel in ['1', '2']:
            with self.subTest(screen=cancel):
                result, calls, notes, root, *_ = self.run_script(whiptail=True, terminal=True,
                                                                 extra={'WHIPTAIL_CANCEL': cancel})
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn('Nothing was changed.', result.stdout)
                self.assertFalse(self.created(calls))
                self.assertFalse([call for call in calls if call[0] in ('pveam',) and call[1] != 'list'])
                self.assertFalse([call for call in calls if call[:2] in (['pct', 'push'], ['pct', 'set'])])

    def test_whiptail_advanced_asks_each_setting_and_keeps_secrets_off_command_lines(self):
        password = 'correct-horse-battery'
        answers = {'Message Link Server ' + VERSION: 'Advanced', 'Hostname': 'chat', 'Disk storage': 'tank',
                   'Memory': '6144', 'Backups': '{root}/messages backups', 'Page password': password,
                   'Tailscale auth key': 'tskey-auth-abcdefgh1234'}
        result, calls, notes, root, *_ = self.run_script(
            whiptail=True, terminal=True, extra={'WHIPTAIL_ANSWERS': json.dumps(answers)})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        titles = [screen[screen.index('--title') + 1] for screen in self.whiptail_calls(calls)]
        self.assertEqual(titles, ['Message Link Server ' + VERSION, 'Container id', 'Hostname', 'Template storage',
                                  'Disk storage', 'Disk size', 'Memory', 'Processor cores', 'Network bridge', 'Address',
                                  'Backups', 'Tailscale', 'Tailscale auth key', 'Page password', 'Page password',
                                  'Make the container'])
        for screen in self.whiptail_calls(calls):
            if screen[screen.index('--title') + 1] in ('Page password', 'Tailscale auth key'):
                self.assertIn('--passwordbox', screen)
        [create] = self.created(calls)
        options = create[4:]
        self.assertEqual(options[options.index('--hostname') + 1], 'chat')
        self.assertEqual(options[options.index('--rootfs') + 1], 'tank:8')
        self.assertEqual(options[options.index('--memory') + 1], '6144')
        self.assertEqual(options[options.index('--mp0') + 1],
                         f'{root}/messages backups,mp=/var/lib/messages-server/backups,backup=0')
        environment = (root / 'ct/105/etc/messages-server/environment').read_text()
        self.assertIn(f'MESSAGES_ADMIN_PASSWORD="{password}"', environment)
        self.assertIn('MESSAGES_TAILNET_AUTHKEY=tskey-auth-abcdefgh1234', environment)
        everything = json.dumps(calls) + (notes or '') + result.stdout + result.stderr
        self.assertNotIn(password, everything)
        self.assertNotIn('tskey-auth-abcdefgh1234', everything)

    def test_whiptail_rejects_a_bad_value_and_asks_again(self):
        answers = {'Message Link Server ' + VERSION: 'Advanced', 'Hostname': 'Not_Valid'}
        # The stand-in gives the same bad hostname each time; Cancel on the third hostname ends the run.
        result, calls, *_ = self.run_script(
            whiptail=True, terminal=True, extra={'WHIPTAIL_ANSWERS': json.dumps(answers), 'WHIPTAIL_CANCEL': '7'})
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        titles = [screen[screen.index('--title') + 1] for screen in self.whiptail_calls(calls)]
        self.assertEqual(titles[:7], ['Message Link Server ' + VERSION, 'Container id', 'Hostname', 'Check this value',
                                      'Hostname', 'Check this value', 'Hostname'])
        self.assertIn('Nothing was changed.', result.stdout)
        self.assertFalse(self.created(calls))

    def test_no_terminal_without_yes_is_refused(self):
        result, calls, *_ = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('no terminal to ask in', result.stderr)
        self.assertFalse([call for call in calls if call[0] not in ('id', 'uname')])

    def test_help_prints_without_a_script_file(self):
        result = subprocess.run(['bash', '-c', SCRIPT.read_text(), '_', '--help'], capture_output=True, text=True,
                                timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('releases/latest/download/install-proxmox.sh', result.stdout)
        self.assertIn('--tarball', result.stdout)
        self.assertIn('--no-google', result.stdout)

    def test_new_prose_has_no_em_dashes(self):
        self.assertNotIn(chr(0x2014), SCRIPT.read_text())


if __name__ == '__main__':
    unittest.main(verbosity=2)
