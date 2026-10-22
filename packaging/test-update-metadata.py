"""Metadata and generated verification contracts for automatic releases."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
import json
import xml.etree.ElementTree as ET
from unittest.mock import patch
spec=importlib.util.spec_from_file_location('package',Path(__file__).with_name('package.py'))
package=importlib.util.module_from_spec(spec);spec.loader.exec_module(package)
metadata_spec=importlib.util.spec_from_file_location('release_metadata',Path(__file__).with_name('release-metadata.py'))
metadata=importlib.util.module_from_spec(metadata_spec);metadata_spec.loader.exec_module(metadata)

class UpdateMetadataTests(unittest.TestCase):
    def test_source_release_metadata_binds_runtime_notice_and_exact_archive_bytes(self):
        self.assertTrue(hasattr(metadata,'source_metadata'),'Separate source assets must be bound into public metadata')
        with tempfile.TemporaryDirectory() as temp:
            dist=Path(temp);payload=dist/'Citrus-win-x64';payload.mkdir()
            archive=dist/'Citrus-ThirdParty-Sources.zip';archive.write_bytes(b'complete corresponding sources')
            record={'schema':1,'version':'0.3.0-preview.3','archive':{
                'file':archive.name,'bytes':archive.stat().st_size,'sha256':metadata.sha(archive),
                'url':'https://github.com/Skytuhua/Citrus/releases/download/v0.3.0-preview.3/'+archive.name},'files':{}}
            for path in [dist/'source-distribution.json',payload/'source-distribution.json']:
                path.write_text(json.dumps(record),encoding='utf-8')
            self.assertEqual(metadata.source_metadata(dist,'0.3.0-preview.3'),record['archive'])
            for field,value in [('version','0.3.0-preview.2'),('url','https://example.com/source.zip'),('bytes',True)]:
                changed=json.loads(json.dumps(record))
                if field=='version':changed[field]=value
                else:changed['archive'][field]=value
                (dist/'source-distribution.json').write_text(json.dumps(changed),encoding='utf-8')
                with self.subTest(field=field),self.assertRaises(ValueError):metadata.source_metadata(dist,'0.3.0-preview.3')
            (dist/'source-distribution.json').write_text(json.dumps(record),encoding='utf-8')
            archive.write_bytes(b'corrupt source archive')
            with self.assertRaises(ValueError):metadata.source_metadata(dist,'0.3.0-preview.3')

    def test_nsis_version_is_generated_from_authoritative_props_and_license_hash(self):
        self.assertTrue(hasattr(package,'version_include'),'Package generation must derive updater metadata from central version props')
        actual=package.version_include()
        props=ET.parse(package.REPO/'CitrusVersion.props').getroot()
        self.assertIn('!define CITRUS_VERSION "'+props.findtext('./PropertyGroup/Version')+'"',actual)
        self.assertIn('!define CITRUS_FILE_VERSION "'+props.findtext('./PropertyGroup/FileVersion')+'"',actual)
        self.assertIn(package.sha(package.REPO/'packaging/notices/presentation/Windows-SDK-License.rtf'),actual)

    def test_every_written_owned_file_gets_a_hash_check_before_commit(self):
        self.assertTrue(hasattr(package,'verification_instructions'),'Installer commit requires independent installed-byte verification')
        text=package.verification_instructions({'one.txt':'a'*64,'folder/two.txt':'b'*64})
        self.assertIn('Push "$INSTDIR\\one.txt"',text)
        self.assertIn('Push "$INSTDIR\\folder\\two.txt"',text)
        self.assertEqual(text.count('Call ${PREFIX}HashFile'),2)
        self.assertEqual(text.count('Call ${PREFIX}Fail'),2)
        with self.assertRaises(ValueError):package.verification_instructions({'../escape':'a'*64})

if __name__=='__main__':unittest.main()
