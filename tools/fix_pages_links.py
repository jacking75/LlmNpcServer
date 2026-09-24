"""Point documentation links outside docs/ at GitHub source files for Pages."""

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOCS = ROOT / "docs"
REPOSITORY = "https://github.com/jacking75/LlmNpcServer/blob/main/"
pattern = re.compile(r'href="([^"]+)"')
changed = 0

for file in DOCS.rglob("*.html"):
    source = file.read_bytes().decode("utf-8")

    def fix(match):
        global changed
        href = match.group(1)
        if not href.startswith("../"):
            return match.group(0)
        raw, sep, fragment = href.partition("#")
        target = (file.parent / raw).resolve()
        if target.is_relative_to(DOCS) or not target.is_relative_to(ROOT):
            return match.group(0)
        changed += 1
        url = REPOSITORY + target.relative_to(ROOT).as_posix()
        return f'href="{url}{sep}{fragment}"'

    result = pattern.sub(fix, source)
    if result != source:
        file.write_bytes(result.encode("utf-8"))

print(f"GitHub Pages source links updated: {changed}")
