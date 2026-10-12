"""Release safety checks, with a fake GitHub service. Never publishes anything."""
import importlib.util
import contextlib
import io
from pathlib import Path
import hashlib
import json
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location('release', Path(__file__).resolve().parents[1] / 'scripts/local-release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.enterContext(contextlib.redirect_stdout(io.StringIO()))
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.root_patch = patch.object(release, 'ROOT', self.root)
        self.root_patch.start()
        self.addCleanup(self.root_patch.stop)

    def payload(self):
        folder = self.root / 'dist/releases/v1.2.3/assets'
        folder.mkdir(parents=True)
        models, rows = [], []
        for i in range(7):
            model = {'Id': 'board-' + str(i), 'AssetName': f'clawpal-{i}.bin', 'MaxBytes': 1024, 'Layout': 'ota-v1'}
            data = bytearray(400)
            data[0] = 0xe9
            data[12] = 9
            data[100:100+len(model['Id'])+1] = (model['Id'] + '\0').encode()
            data[150:156] = b'1.2.3\0'
            file = folder / model['AssetName']
            file.write_bytes(data)
            rows.append({'schema': 1, 'version': '1.2.3', 'board': model['Id'], 'layout': 'ota-v1',
                         'firmware': {'file': file.name, 'bytes': len(data), 'sha256': release.sha(file)}})
            models.append(model)
        (folder / 'ClawPal-Setup.exe').write_bytes(b'MZtest-installer')
        (folder / 'ClawPal-Setup.exe.sha256').write_text(release.sha(folder / 'ClawPal-Setup.exe') + '  ClawPal-Setup.exe\n')
        (folder / 'clawpal-release.json').write_text(json.dumps({'schema': 2, 'version': '1.2.3', 'boards': rows}))
        catalog = {'Boards': models}
        (self.root / 'boards').mkdir()
        (self.root / 'boards/catalog.json').write_text(json.dumps(catalog))
        return folder, catalog

    def bundle(self, folder):
        names = [*release.SOURCES, release.SDK_NAME, 'ClawPal-source.zip', 'ClawPal-licenses.zip']
        with zipfile.ZipFile(folder / release.SOURCE_NAME, 'w') as output:
            for name in names:
                output.writestr(name, b'source-test')
            output.writestr('README.txt', 'source bundle')
            output.writestr('SHA256SUMS', ''.join(hashlib.sha256(b'source-test').hexdigest() + '  ' + n + '\n' for n in names))

    def plan(self):
        folder, catalog = self.payload()
        self.bundle(folder)
        names = release.validate_payload(folder, '1.2.3', catalog) + [release.SOURCE_NAME]
        (folder.parent / 'release-plan.json').write_text(json.dumps({'version': '1.2.3', 'commit': 'abc',
                    'assets': {n: release.sha(folder / n) for n in names}, 'notes': 'Release notes'}))
        return folder

    def test_seven_board_package(self):
        folder, catalog = self.payload()
        self.assertEqual(len(release.validate_payload(folder, '1.2.3', catalog)), 10)

    def test_rejects_changed_firmware(self):
        folder, catalog = self.payload()
        with (folder / 'clawpal-3.bin').open('ab') as stream:
            stream.write(b'changed')
        with self.assertRaisesRegex(ValueError, 'size'):
            release.validate_payload(folder, '1.2.3', catalog)

    def test_rejects_missing_board(self):
        folder, catalog = self.payload()
        file = folder / 'clawpal-release.json'
        manifest = json.loads(file.read_text())
        manifest['boards'].pop()
        file.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, 'board firmware'):
            release.validate_payload(folder, '1.2.3', catalog)

    def test_rejects_changed_installer(self):
        folder, catalog = self.payload()
        (folder / 'ClawPal-Setup.exe').write_bytes(b'MZmodified')
        with self.assertRaisesRegex(ValueError, 'Installer checksum'):
            release.validate_payload(folder, '1.2.3', catalog)

    def test_checksums_reject_traversal_duplicates(self):
        digest = 'a' * 64
        for text in (digest + '  ../private.txt', (digest + '  source.tar.gz\n') * 2):
            with self.assertRaises(ValueError):
                release.checksum_map(text)
        self.assertEqual(release.checksum_map(digest + '  dist/sources/sdk.tar.gz'), {'sdk.tar.gz': digest})

    def test_bundle_requires_legacy_stub(self):
        folder = self.root
        self.bundle(folder)
        release.validate_bundle(folder / release.SOURCE_NAME)
        with zipfile.ZipFile(folder / release.SOURCE_NAME, 'w') as bundle:
            bundle.writestr('ClawPal-source.zip', b'source')
        with self.assertRaisesRegex(ValueError, 'Incomplete'):
            release.validate_bundle(folder / release.SOURCE_NAME)

    def test_identical_upload_skipped(self):
        file = self.root / 'test.zip'
        file.write_bytes(b'same')
        with patch.object(release, 'request', return_value=[{'name': file.name, 'size': 4, 'digest': 'sha256:' + release.sha(file)}]), \
                patch.object(release.http.client, 'HTTPSConnection') as connection:
            release.ensure_asset(1, file, 'test')
            connection.assert_not_called()

    def test_existing_different_upload_never_overwritten(self):
        file = self.root / 'test.zip'
        file.write_bytes(b'new')
        with patch.object(release, 'request', return_value=[{'name': file.name, 'size': 3, 'digest': 'sha256:old'}]), \
                patch.object(release.http.client, 'HTTPSConnection') as connection:
            with self.assertRaisesRegex(ValueError, 'differs'):
                release.ensure_asset(1, file, 'test')
            connection.assert_not_called()

    def test_streamed_upload_checks_github_digest(self):
        file = self.root / 'source.zip'
        file.write_bytes(b'archive')
        with patch.object(release, 'request', return_value=[]), \
                patch.object(release.http.client, 'HTTPSConnection') as connection:
            response = connection.return_value.getresponse.return_value
            response.status = 201
            response.read.return_value = json.dumps({'digest': 'sha256:' + release.sha(file), 'size': 7}).encode()
            release.ensure_asset(1, file, 'test')
            call = connection.return_value.request.call_args
            self.assertEqual(call.args[0], 'POST')
            self.assertEqual(call.kwargs['headers']['Content-Length'], '7')
            connection.return_value.close.assert_called_once()

    def test_published_version_rejected_before_git_push(self):
        self.plan()
        with patch.object(release, 'preflight', return_value=('1.2.3', 'abc')), \
                patch.object(release, 'auth', return_value='test'), \
                patch.object(release, 'request', return_value={'draft': False}), \
                patch.object(release, 'git') as git:
            with self.assertRaisesRegex(ValueError, 'already published'):
                release.publish()
            git.assert_not_called()

    def test_failed_upload_keeps_draft(self):
        self.plan()
        calls = []
        def api(path, **kwargs):
            calls.append((path, kwargs.get('method', 'GET')))
            return {'draft': True, 'id': 1}
        with patch.object(release, 'preflight', return_value=('1.2.3', 'abc')), \
                patch.object(release, 'auth', return_value='test'), \
                patch.object(release, 'request', side_effect=api), \
                patch.object(release, 'git', return_value=''), \
                patch.object(release.subprocess, 'run') as run, \
                patch.object(release, 'ensure_asset', side_effect=ValueError('connection lost')):
            run.return_value.returncode = 1
            with self.assertRaisesRegex(ValueError, 'connection lost'):
                release.publish()
        self.assertFalse(any(method == 'PATCH' for _, method in calls))

    def test_publish_only_after_all_assets(self):
        folder = self.plan()
        uploaded = []
        def api(path, **kwargs):
            if '/assets?' in path:
                return [{'name': name} for name in uploaded]
            if kwargs.get('method') == 'PATCH':
                self.assertEqual(len(uploaded), 11)
                return {'html_url': 'https://github.com/Kaayoos/clawpal/releases/tag/v1.2.3'}
            return {'draft': True, 'id': 1}
        with patch.object(release, 'preflight', return_value=('1.2.3', 'abc')), \
                patch.object(release, 'auth', return_value='test'), \
                patch.object(release, 'request', side_effect=api), \
                patch.object(release, 'git', return_value=''), \
                patch.object(release.subprocess, 'run') as run, \
                patch.object(release, 'ensure_asset', side_effect=lambda _, file, token: uploaded.append(file.name)):
            run.return_value.returncode = 1
            release.publish()

    def test_repair_is_append_only(self):
        digest = hashlib.sha256(b'archive').hexdigest()
        sums = {name: digest for name in [*release.SOURCES, release.SDK_NAME]}
        missing = 'esptool-legacy-flasher-stub-v1.11.1.tar.gz'
        assets = [{'name': name, 'digest': 'sha256:' + digest} for name in sums if name != missing]
        assets.append({'name': 'SHA256SUMS', 'digest': 'sha256:' + digest, 'browser_download_url': 'https://github.com/example'})
        def get_sums(url, file, expected):
            file.parent.mkdir(parents=True)
            file.write_text(''.join(value + '  ' + name + '\n' for name, value in sums.items()))
        with patch.object(release, 'version', return_value='0.8.1'), \
                patch.object(release, 'request', return_value={'id': 1, 'assets': assets}), \
                patch.object(release, 'download', side_effect=get_sums), \
                patch.object(release, 'source_archive', side_effect=lambda name, file, expected: file.write_bytes(b'archive')), \
                patch.object(release, 'auth', return_value='test'), \
                patch.object(release, 'ensure_asset') as upload:
            release.repair(True)
        self.assertEqual(upload.call_count, 1)
        self.assertEqual(upload.call_args.args[1].name, missing)


if __name__ == '__main__':
    unittest.main()
