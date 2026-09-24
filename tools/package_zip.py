"""Create a portable zip with Unix paths and executable permissions."""

import os
from pathlib import Path
import shutil
import stat
import sys
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


EXECUTABLES = {
    "bin/host/npc-server",
    "bin/cli/npc",
    "bin/studio/npc-studio",
    "bin/conformance/npc-conformance",
    "bin/test-game-server/npc-test-gs",
}


def add_entry(archive: ZipFile, path: Path, name: str, executable: bool) -> None:
    info = ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
    info.create_system = 3
    info.compress_type = ZIP_DEFLATED
    info.external_attr = ((stat.S_IFREG | (0o755 if executable else 0o644)) << 16)
    with path.open("rb") as source, archive.open(info, "w") as target:
        shutil.copyfileobj(source, target, length=1024 * 1024)


def add_directory(archive: ZipFile, name: str) -> None:
    info = ZipInfo(name.rstrip("/") + "/", date_time=(1980, 1, 1, 0, 0, 0))
    info.create_system = 3
    info.external_attr = ((stat.S_IFDIR | 0o755) << 16) | 0x10
    archive.writestr(info, b"")


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: package_zip.py <package-dir> <output.zip>", file=sys.stderr)
        return 2

    package = Path(sys.argv[1]).resolve(strict=True)
    output = Path(sys.argv[2]).resolve()
    if not package.is_dir() or output.is_relative_to(package):
        print("package-dir must be a directory and output.zip must be outside it", file=sys.stderr)
        return 2

    with ZipFile(output, "w", allowZip64=True) as archive:
        for current, dirs, files in os.walk(package):
            dirs.sort()
            files.sort()
            relative = Path(current).relative_to(package)
            name = (Path(package.name) / relative).as_posix()
            add_directory(archive, name)
            for file in files:
                path = Path(current) / file
                relative_file = (relative / file).as_posix()
                add_entry(
                    archive,
                    path,
                    (Path(package.name) / relative / file).as_posix(),
                    relative_file in EXECUTABLES,
                )
    print(f"zip created: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
