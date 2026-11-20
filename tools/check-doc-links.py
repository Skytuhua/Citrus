#!/usr/bin/env python3
"""Check relative documentation link and image targets offline.

Scans project-owned Markdown and reports links whose repository target does not
exist. It never contacts a network host and uses only the standard library.

Scan roots: every tracked ``*.md`` file (``git ls-files``) under ``--root``. When
the root is not a Git checkout, such as a test fixture, Markdown files are found
by walking the directory, skipping hidden, ``bin``, ``obj`` and ``TestResults``
directories. Vendored upstream texts under ``packaging/notices/`` are excluded, as
is ``packaging/Distribution-README.md``: ``packaging/package.py`` installs it as
the payload's ``README.md``, so its links resolve against the installed layout.

Checked forms: inline Markdown links and images, ``[text](target)`` and
``![alt](target)``, including ``<target>`` destinations and titles, plus quoted
HTML ``href`` and ``src`` attributes. Relative targets resolve from the
containing document, and a leading ``/`` resolves from the repository root.
For ``file.md#section`` only the file is checked. Percent-encoded paths are
decoded before the check. Targets must stay inside the root and must match the
on-disk letter case, as they do on GitHub and Linux.

Ignored: same-document ``#anchors``, scheme and protocol-relative URLs
(``https:``, ``mailto:``, ``//host``), fenced code blocks and inline code spans.

Not supported in this version: reference-style links and definitions, autolinks,
heading/anchor slugs, ``srcset``, indented code blocks, HTML comments, links that
span lines, and external availability.
"""
import argparse
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import unquote

# Vendored upstream license texts, and the payload guide that packaging/package.py
# copies to the installed root as README.md, where its docs/ links resolve.
EXCLUDED_PREFIXES = ('packaging/notices/', 'packaging/Distribution-README.md')
WALK_SKIPPED_DIRECTORIES = {'bin', 'obj', 'TestResults', 'node_modules'}

FENCE = re.compile(r'^ {0,3}(`{3,}|~{3,})(.*)$')
CODE_SPAN = re.compile(r'(`+)(.+?)\1')
HTML_TAG = re.compile(r'<[A-Za-z][^<>]*>')
HTML_ATTRIBUTE = re.compile(r'''(?<![\w-])(href|src)\s*=\s*(["'])(.*?)\2''', re.IGNORECASE)
SCHEME = re.compile(r'^[A-Za-z][A-Za-z0-9+.-]*:')
INVALID_ESCAPE = re.compile(r'%(?![0-9A-Fa-f]{2})')
MARKDOWN_ESCAPE = re.compile(r'\\([!-/:-@\[-`{-~])')


class Problem:
    def __init__(self, document, line, target, reason):
        self.document = document
        self.line = line
        self.target = target
        self.reason = reason

    def __str__(self):
        return f'{self.document}:{self.line}: {self.reason}: {self.target}'


def markdown_files(root):
    """Return repository-relative POSIX paths of the Markdown files to scan."""
    if (root / '.git').exists():
        listed = subprocess.run(['git', '-C', str(root), 'ls-files', '-z', '--', '*.md'],
                                check=True, capture_output=True).stdout.decode('utf-8')
        paths = [path for path in listed.split('\0') if path]
    else:
        paths = []
        for directory, names, files in os.walk(root):
            names[:] = sorted(name for name in names
                              if not name.startswith('.') and name not in WALK_SKIPPED_DIRECTORIES)
            for name in sorted(files):
                if name.endswith('.md'):
                    paths.append((Path(directory) / name).relative_to(root).as_posix())
    return sorted(path for path in paths
                  if not path.startswith(EXCLUDED_PREFIXES) and (root / path).is_file())


def live_lines(text):
    """Yield (line number, text) outside fenced code, with inline code removed."""
    fence = None
    for number, line in enumerate(text.splitlines(), 1):
        match = FENCE.match(line)
        if fence:
            if match and match.group(1)[0] == fence[0] and len(match.group(1)) >= len(fence) \
                    and not match.group(2).strip():
                fence = None
            continue
        if match and not (match.group(1)[0] == '`' and '`' in match.group(2)):
            fence = match.group(1)
            continue
        yield number, CODE_SPAN.sub(lambda span: ' ' * len(span.group(0)), line)


def inline_destinations(line):
    """Yield destinations of inline Markdown links and images on one line."""
    start = 0
    while (opening := line.find('](', start)) >= 0:
        position = opening + 2
        while position < len(line) and line[position] in ' \t':
            position += 1
        if position < len(line) and line[position] == '<':
            closing = line.find('>', position + 1)
            if closing < 0:
                start = position
                continue
            yield line[position + 1:closing]
            start = closing + 1
            continue
        depth = 0
        end = position
        while end < len(line):
            character = line[end]
            if character == '\\' and end + 1 < len(line):
                end += 2
                continue
            if character in ' \t':
                break
            if character == '(':
                depth += 1
            elif character == ')':
                if depth == 0:
                    break
                depth -= 1
            end += 1
        if end > position:
            yield MARKDOWN_ESCAPE.sub(r'\1', line[position:end])
        start = max(end, opening + 2)


def html_destinations(line):
    for tag in HTML_TAG.finditer(line):
        for attribute in HTML_ATTRIBUTE.finditer(tag.group(0)):
            yield attribute.group(3)


def exact_case_exists(root, relative, listing_cache):
    """True when every component exists with the same letter case as written."""
    current = root
    for part in relative.parts:
        if current not in listing_cache:
            try:
                listing_cache[current] = set(os.listdir(current))
            except OSError:
                return False
        if part not in listing_cache[current]:
            return False
        current = current / part
    return True


def check_target(root, document, target, listing_cache):
    """Return a problem description for a missing repository target, or None."""
    target = target.strip()
    if not target or target.startswith('#') or target.startswith('//') or SCHEME.match(target):
        return None
    path = target.split('#', 1)[0].split('?', 1)[0]
    if not path:
        return None
    if INVALID_ESCAPE.search(path):
        return 'invalid percent-encoding in link path'
    path = unquote(path)
    if '\\' in path:
        return 'backslash in link path; use /'
    base = root if path.startswith('/') else (root / document).parent
    resolved = Path(os.path.normpath(base / path.lstrip('/')))
    if not resolved.is_relative_to(root):
        return 'target resolves outside the repository root'
    relative = resolved.relative_to(root)
    if exact_case_exists(root, relative, listing_cache):
        if resolved.resolve().is_relative_to(root.resolve()):
            return None
        return 'target resolves outside the repository root'
    if resolved.exists():
        return 'letter case does not match the target on disk'
    return 'missing target'


def check(root):
    """Return (problems, documents, links checked) for the Markdown under root."""
    root = Path(os.path.abspath(root))
    documents = markdown_files(root)
    problems = []
    checked = 0
    listing_cache = {}
    for document in documents:
        text = (root / document).read_text(encoding='utf-8-sig')
        for number, line in live_lines(text):
            for target in [*inline_destinations(line), *html_destinations(line)]:
                checked += 1
                reason = check_target(root, document, target, listing_cache)
                if reason:
                    problems.append(Problem(document, number, target, reason))
    return problems, documents, checked


def main(arguments=None):
    parser = argparse.ArgumentParser(description='Check relative Markdown link and image targets offline.')
    parser.add_argument('--root', default=Path(__file__).resolve().parents[1], type=Path,
                        help='repository root to scan (default: this checkout)')
    options = parser.parse_args(arguments)
    if not options.root.is_dir():
        parser.error(f'root is not a directory: {options.root}')
    problems, documents, checked = check(options.root)
    for problem in problems:
        print(problem)
    if problems:
        print(f'{len(problems)} broken documentation target(s) in {len(documents)} Markdown file(s).', file=sys.stderr)
        return 1
    print(f'Checked {checked} link(s) in {len(documents)} Markdown file(s); no missing relative targets.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
