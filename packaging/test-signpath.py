"""Offline contracts for optional, installer-only SignPath signing."""
import importlib.util
import contextlib
import io
import json
import os
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('release_metadata', ROOT / 'packaging/release-metadata.py')
metadata = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(metadata)
THUMBPRINT = 'A' * 40
VALID = {'status': 'Valid', 'signatureType': 'Authenticode', 'signer': {'subject': 'CN=SignPath Foundation', 'thumbprint': THUMBPRINT},
         'timestamp': {'subject': 'CN=Timestamp Authority', 'thumbprint': 'B' * 40}}


class SignatureMetadataTests(unittest.TestCase):
    def test_unsigned_is_reported_from_the_actual_probe_and_cannot_pass_signed_mode(self):
        self.assertTrue(hasattr(metadata, 'signature_metadata'), 'Release metadata must inspect Authenticode status')
        actual = metadata.signature_metadata({'status': 'NotSigned', 'signer': None, 'timestamp': None})
        self.assertEqual(actual, {'signature': 'NotSigned'})
        with self.assertRaisesRegex(ValueError, 'Valid'):
            metadata.signature_metadata({'status': 'NotSigned'}, require_signed=True, allowed_thumbprints=THUMBPRINT)

    def test_signed_mode_requires_valid_trust_an_exact_allowed_signer_and_a_timestamp(self):
        self.assertTrue(hasattr(metadata, 'signature_metadata'), 'Signed output must be verified before release')
        actual = metadata.signature_metadata(VALID, require_signed=True, allowed_thumbprints=THUMBPRINT.lower())
        self.assertEqual(actual['signature'], 'Valid')
        self.assertEqual(actual['signer']['thumbprint'], THUMBPRINT)
        self.assertEqual(actual['signatureScope'], 'installer-only')
        with self.assertRaisesRegex(ValueError, 'approved'):
            metadata.signature_metadata(VALID, require_signed=True, allowed_thumbprints='C' * 40)
        with self.assertRaisesRegex(ValueError, 'timestamp'):
            metadata.signature_metadata({**VALID, 'timestamp': None}, require_signed=True, allowed_thumbprints=THUMBPRINT)
        for status in ['HashMismatch', 'NotTrusted', 'UnknownError', 'NotSupportedFileFormat']:
            with self.subTest(status=status), self.assertRaisesRegex(ValueError, 'Invalid Authenticode'):
                metadata.signature_metadata({**VALID, 'status': status})

    def test_signer_pins_are_mandatory_and_malformed_configuration_is_rejected(self):
        self.assertTrue(hasattr(metadata, 'approved_thumbprints'), 'Signer certificate pins must be explicit')
        self.assertEqual(metadata.approved_thumbprints(THUMBPRINT.lower() + ', ' + 'B' * 40), {THUMBPRINT, 'B' * 40})
        for pins in ['', 'not-a-thumbprint', THUMBPRINT + ',', '*', 'A' * 64]:
            with self.subTest(pins=pins), self.assertRaisesRegex(ValueError, 'thumbprint'):
                metadata.approved_thumbprints(pins)

    @unittest.skipUnless(os.name == 'nt', 'Authenticode trust is provided by Windows')
    def test_real_windows_probe_reports_unsigned_bytes_and_preserves_literal_filenames(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.local') as temp:
            candidate = Path(temp) / "Citrus $literal 'quote' `tick.exe"
            candidate.write_bytes(pe_fixture())
            actual = metadata.authenticode_signature(candidate)
            self.assertEqual(actual['status'], 'NotSigned')
            self.assertEqual(metadata.signature_metadata(actual), {'signature': 'NotSigned'})

    def test_metadata_and_checksums_describe_final_installer_bytes(self):
        self.assertTrue(hasattr(metadata, 'authenticode_signature'), 'Metadata needs an actual Windows signature probe')
        with tempfile.TemporaryDirectory(dir=ROOT / '.local') as temp:
            dist = Path(temp)
            (dist / 'Citrus-win-x64').mkdir()
            (dist / 'Citrus-win-x64/install-inventory.json').write_text('{}')
            (dist / 'Citrus-Setup.exe').write_bytes(b'final signed installer')
            props = (ROOT / 'CitrusVersion.props').read_bytes()
            version = metadata.ET.fromstring(props).findtext('./PropertyGroup/Version')
            with patch.object(metadata, 'installer_file_version', return_value=version), \
                    patch.object(metadata, 'authenticode_signature', return_value=VALID), \
                    patch.object(metadata.subprocess, 'run'), \
                    patch.object(metadata.subprocess, 'check_output', return_value=props):
                with contextlib.redirect_stdout(io.StringIO()):
                    metadata.write_release(dist, 'a' * 40, require_signed=True, allowed_thumbprints=THUMBPRINT)
            actual = json.loads((dist / 'release-manifest.json').read_text())
            self.assertEqual(actual['installer']['signature'], 'Valid')
            self.assertEqual(actual['installer']['sha256'], metadata.sha(dist / 'Citrus-Setup.exe'))
            self.assertIn(actual['installer']['sha256'] + '  Citrus-Setup.exe', (dist / 'release/SHA256SUMS').read_text())


def pe_fixture():
    image = bytearray(533)
    image[:2] = b'MZ'
    struct.pack_into('<I', image, 60, 128)
    image[128:132] = b'PE\0\0'
    struct.pack_into('<H', image, 148, 224)
    struct.pack_into('<H', image, 152, 0x10b)
    image[512:] = b'own installer payload'
    return image


def signed_fixture(image):
    signed = bytearray(image)
    offset = (len(image) + 7) & ~7
    signed += b'\0' * (offset - len(image)) + struct.pack('<IHH', 16, 0x200, 2) + b'testcert'
    struct.pack_into('<I', signed, 152 + 64, 123)
    struct.pack_into('<II', signed, 152 + 128, offset, 16)
    return signed


class ReturnedInstallerTests(unittest.TestCase):
    def setUp(self):
        script = ROOT / 'packaging/signpath/verify-installer.py'
        self.assertTrue(script.is_file(), 'Signed output needs a verifier that refuses unsafe replacement')
        spec = importlib.util.spec_from_file_location('signpath_verifier', script)
        self.verifier = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.verifier)

    def test_only_authenticode_changes_are_accepted_not_a_different_installer(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.local') as temp:
            original, returned = Path(temp) / 'original.exe', Path(temp) / 'returned.exe'
            image = pe_fixture()
            original.write_bytes(image)
            signed = signed_fixture(image)
            returned.write_bytes(signed)
            self.verifier.verify_unchanged_payload(original, returned)
            signed[520] ^= 1
            returned.write_bytes(signed)
            with self.assertRaisesRegex(ValueError, 'payload'):
                self.verifier.verify_unchanged_payload(original, returned)

    def test_missing_extra_or_untrusted_output_never_replaces_the_unsigned_installer(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.local') as temp:
            root = Path(temp)
            target = root / 'Citrus-Setup.exe'
            target.write_bytes(pe_fixture())
            before = target.read_bytes()
            signed = root / 'signed'
            signed.mkdir()
            with self.assertRaisesRegex(ValueError, 'exactly'):
                self.verifier.accept_installer(signed, target, '0.3.0-preview.2', THUMBPRINT)
            candidate = signed / 'Citrus-Setup.exe'
            candidate.write_bytes(signed_fixture(before))
            (signed / 'extra.exe').write_bytes(b'extra')
            with self.assertRaisesRegex(ValueError, 'exactly'):
                self.verifier.accept_installer(signed, target, '0.3.0-preview.2', THUMBPRINT)
            (signed / 'extra.exe').unlink()
            with patch.object(self.verifier.metadata, 'authenticode_signature', return_value={**VALID, 'status': 'NotTrusted'}):
                with self.assertRaisesRegex(ValueError, 'Invalid Authenticode'):
                    self.verifier.accept_installer(signed, target, '0.3.0-preview.2', THUMBPRINT)
            self.assertEqual(target.read_bytes(), before)

    def test_verified_output_replaces_only_the_outer_installer(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.local') as temp:
            root = Path(temp)
            target = root / 'Citrus-Setup.exe'
            target.write_bytes(pe_fixture())
            candidate = root / 'signed/Citrus-Setup.exe'
            candidate.parent.mkdir()
            candidate.write_bytes(signed_fixture(target.read_bytes()))
            probe = {**VALID, 'productName': 'Citrus', 'fileVersion': '0.3.0-preview.2'}
            with patch.object(self.verifier.metadata, 'authenticode_signature', return_value=probe):
                self.verifier.accept_installer(candidate.parent, target, '0.3.0-preview.2', THUMBPRINT)
            self.assertEqual(target.read_bytes(), candidate.read_bytes())

    def test_preflight_requires_all_service_configuration_without_printing_secrets(self):
        with self.assertRaisesRegex(ValueError, 'SIGNPATH'):
            self.verifier.preflight({})


if __name__ == '__main__':
    unittest.main()
