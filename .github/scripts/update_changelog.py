#!/usr/bin/env python3
"""Turns "## [Unreleased]" in CHANGELOG.md into the released version.

Called by .github/workflows/release.yml after the GitHub release:
  update_changelog.py <version> <date YYYY-MM-DD> <notes.md>

- The entries under [Unreleased] move to "## [<version>] – <date>", an empty [Unreleased] stays on top.
- If [Unreleased] is empty, the generated release notes (commit subjects) are used instead.
- Does nothing if the version is already in the file.
"""
import re
import sys

PATH = "CHANGELOG.md"
HEAD = "## [Unreleased]"


def main():
    version, date, notes_path = sys.argv[1:4]
    with open(PATH, encoding="utf-8") as f:
        text = f.read()
    if f"## [{version}]" in text:
        print(f"CHANGELOG.md already contains {version}")
        return
    start = text.find(HEAD)
    if start < 0:
        sys.exit("CHANGELOG.md has no '## [Unreleased]' section")
    body_start = start + len(HEAD)
    nxt = re.search(r"^## \[", text[body_start:], re.M)
    body_end = body_start + nxt.start() if nxt else len(text)
    body = text[body_start:body_end].strip()
    if not body:
        with open(notes_path, encoding="utf-8") as f:
            body = f.read().strip() or "Keine Änderungen."
    section = f"{HEAD}\n\n## [{version}] – {date}\n\n{body}\n\n"
    with open(PATH, "w", encoding="utf-8", newline="\n") as f:
        f.write(text[:start] + section + text[body_end:])
    print(f"CHANGELOG.md: [Unreleased] -> [{version}] – {date}")


if __name__ == "__main__":
    main()
