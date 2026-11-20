#!/usr/bin/env python3
"""Offline contracts for tools/check-doc-links.py; every fixture is a temporary tree."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

CHECKER = Path(__file__).resolve().parents[1] / 'check-doc-links.py'
spec = importlib.util.spec_from_file_location('check_doc_links', CHECKER)
links = importlib.util.module_from_spec(spec)
spec.loader.exec_module(links)


class DocumentationLinkTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        # The repository root is one level down so fixtures can place files beside it.
        self.root = Path(self.temporary.name) / 'repo'
        self.write('README.md', '# Fixture\n')
        self.write('docs/guide.md', '# Guide\n')
        self.write('docs/images/mark.png', 'not an image; existence is all that is checked')
        self.write('LICENSE', 'MIT\n')

    def write(self, relative, text):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8', newline='\n')
        return path

    def run_checker(self):
        output, errors = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
            status = links.main(['--root', str(self.root)])
        return status, output.getvalue().splitlines(), errors.getvalue()

    def assert_clean(self):
        status, output, errors = self.run_checker()
        self.assertEqual((status, errors), (0, ''), output)
        self.assertTrue(output[-1].startswith('Checked '), output)

    def assert_problems(self, *expected):
        status, output, errors = self.run_checker()
        self.assertEqual(status, 1)
        self.assertEqual(output, list(expected))
        self.assertIn(f'{len(expected)} broken documentation target(s)', errors)

    def test_valid_files_images_paths_fragments_anchors_and_html_pass(self):
        self.write('docs/nested/page.md', '\n'.join([
            '[Guide](../guide.md) and [root readme](../../README.md)',
            '![Mark](../images/mark.png "Title")',
            '[Repository root](/LICENSE) and [root image](/docs/images/mark.png)',
            '[Section](../guide.md#some-heading) and [same page](#local-anchor)',
            '<a href="../guide.md">HTML link</a> <img src=\'/docs/images/mark.png\' alt="">',
            '[Folder](../images/) and [![badge](../images/mark.png)](../guide.md)',
            '[Angle](<../guide.md> "title") [Query](../guide.md?plain=1)',
            '[Web](https://example.invalid/missing.md) [Mail](mailto:someone@example.invalid)',
            '<a href="//example.invalid/missing.png">protocol-relative</a> [Empty]()',
        ]) + '\n')
        self.assert_clean()

    def test_missing_files_and_images_report_source_and_line(self):
        self.write('docs/page.md', '\n'.join([
            '# Page',
            '[Missing](missing.md)',
            'Text ![Missing image](images/absent.png) and [ok](guide.md).',
            '<img src="images/gone.svg" alt=""> <a href="gone.md">gone</a>',
        ]) + '\n')
        self.assert_problems(
            'docs/page.md:2: missing target: missing.md',
            'docs/page.md:3: missing target: images/absent.png',
            'docs/page.md:4: missing target: images/gone.svg',
            'docs/page.md:4: missing target: gone.md',
        )

    def test_missing_parent_root_and_fragment_targets_fail(self):
        self.write('docs/nested/page.md', '\n'.join([
            '[Parent](../absent.md)',
            '[Root](/docs/absent.md)',
            '[Fragment](../absent.md#section)',
            '[Root-relative is not document-relative](/nested/page.md)',
        ]) + '\n')
        self.assert_problems(
            'docs/nested/page.md:1: missing target: ../absent.md',
            'docs/nested/page.md:2: missing target: /docs/absent.md',
            'docs/nested/page.md:3: missing target: ../absent.md#section',
            'docs/nested/page.md:4: missing target: /nested/page.md',
        )

    def test_fenced_examples_and_inline_code_are_not_live_links(self):
        self.write('docs/page.md', '\n'.join([
            '```markdown',
            '[Sample](missing.md)',
            '```',
            '~~~~',
            '<img src="missing.png">',
            '```',
            'still inside the tilde fence: ![x](missing.png)',
            '~~~~',
            'Use `[text](missing.md)` or ``<a href="x.md">`` in prose.',
            '   ```',
            '[Indented fence sample](missing.md)',
            '   ```',
            '[After the fences](absent-after-fence.md)',
        ]) + '\n')
        self.assert_problems('docs/page.md:13: missing target: absent-after-fence.md')

    def test_encoded_spaces_are_decoded_and_bad_escapes_are_rejected(self):
        self.write('docs/two words.md', '# Spaced\n')
        self.write('docs/page.md', '\n'.join([
            '[Encoded](two%20words.md) [Angle](<two words.md>) [Fragment](two%20words.md#top)',
            '[Missing encoded](three%20words.md)',
            '[Bad escape](two%2words.md)',
        ]) + '\n')
        self.assert_problems(
            'docs/page.md:2: missing target: three%20words.md',
            'docs/page.md:3: invalid percent-encoding in link path: two%2words.md',
        )

    def test_targets_cannot_resolve_outside_the_root(self):
        (self.root.parent / 'outside.md').write_text('# Outside the repository\n', encoding='utf-8')
        self.write('docs/page.md', '\n'.join([
            '[Escape](../../outside.md)',
            '[Root escape](/../outside.md)',
            '<img src="../../../outside.md">',
        ]) + '\n')
        self.assert_problems(
            'docs/page.md:1: target resolves outside the repository root: ../../outside.md',
            'docs/page.md:2: target resolves outside the repository root: /../outside.md',
            'docs/page.md:3: target resolves outside the repository root: ../../../outside.md',
        )

    def test_letter_case_and_backslashes_follow_github_paths(self):
        self.write('docs/page.md', '[Case](Guide.md)\n[Slash](images\\mark.png)\n')
        status, output, _ = self.run_checker()
        self.assertEqual(status, 1)
        self.assertEqual(len(output), 2)
        # Case-insensitive file systems see the file but GitHub and Linux do not.
        self.assertRegex(output[0], r'^docs/page\.md:1: (letter case does not match the target on disk|missing target): Guide\.md$')
        self.assertEqual(output[1], 'docs/page.md:2: backslash in link path; use /: images\\mark.png')

    def test_vendored_notices_distribution_guide_and_build_trees_are_not_scanned(self):
        self.write('packaging/notices/upstream/LICENSE.md', '[Upstream](missing.md)\n')
        self.write('packaging/Distribution-README.md', '[Installed layout](docs/QUICKSTART.md)\n')
        self.write('src/App/bin/Release/README.md', '[Build output](missing.md)\n')
        self.write('.hidden/README.md', '[Hidden](missing.md)\n')
        self.assert_clean()

    @unittest.skipUnless(shutil.which('git'), 'git is required for the tracked-file scan')
    def test_git_checkout_scans_only_tracked_markdown(self):
        self.write('docs/tracked.md', '[Missing](tracked-missing.md)\n')
        self.write('docs/untracked.md', '[Missing](untracked-missing.md)\n')
        git = ["git", "-c", "core.autocrlf=false", "-C", str(self.root)]
        subprocess.run([*git, 'init', '-q'], check=True)
        subprocess.run([*git, 'add', 'README.md', 'docs/guide.md', 'docs/tracked.md'], check=True)
        self.assert_problems('docs/tracked.md:1: missing target: tracked-missing.md')

    def test_command_line_exit_status_and_output(self):
        self.write('docs/page.md', '[Missing](missing.md)\n')
        environment = {**os.environ, 'PYTHONIOENCODING': 'utf-8'}
        failed = subprocess.run([sys.executable, str(CHECKER), '--root', str(self.root)],
                                capture_output=True, text=True, env=environment)
        self.assertEqual(failed.returncode, 1)
        self.assertEqual(failed.stdout, 'docs/page.md:1: missing target: missing.md\n')
        self.write('docs/page.md', '[Present](guide.md)\n')
        passed = subprocess.run([sys.executable, str(CHECKER), '--root', str(self.root)],
                                capture_output=True, text=True, env=environment)
        self.assertEqual((passed.returncode, passed.stderr), (0, ''))
        self.assertIn('no missing relative targets', passed.stdout)


if __name__ == '__main__':
    unittest.main()
