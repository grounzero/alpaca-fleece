#!/usr/bin/env python3
"""
zipcs.py

Recursively scans an input directory and creates a zip archive containing only
selected C#-repo "source/config" files, while excluding common build/cache/vendor
directories and noisy user-specific artifacts.

Includes extensions:
  .cs, .csproj, .sln, .json, .xml, .config, .props, .targets

Excludes directories (pruned early for speed):
  bin/, obj/, .git/, node_modules/, packages/, .vs/

Excludes binaries/archives:
  .dll, .exe, .pdb, .nupkg, .zip

Also excludes common IDE/user/cache/log artifacts:
  *.user, *.suo, *.cache, *.log, *.tmp, *.bak, *.swp, *.DS_Store, Thumbs.db

Special rule:
  Exclude appsettings.*.json (e.g., appsettings.Development.json),
  but keep appsettings.json.

Preserves directory structure inside the zip.

Example usage (your case):
  # from the folder containing zipcs.py and the 'cs' subfolder:
  python3 zipcs.py cs cs_sources.zip

  # paths with spaces:
  python3 zipcs.py "cs" "cs sources.zip"

  # absolute paths:
  python3 zipcs.py "/path/to/repo/cs" "/path/to/out/cs_sources.zip"
"""

from __future__ import annotations

import fnmatch
import sys
import zipfile
from pathlib import Path

# Extensions we want to include in the zip
INCLUDE_EXTS = {
    ".cs",
    ".csproj",
    ".sln",
    ".json",
    ".xml",
    ".config",
    ".props",
    ".targets",
}

# Extensions we explicitly do NOT want (extra safety)
EXCLUDE_EXTS = {
    ".dll",
    ".exe",
    ".pdb",
    ".nupkg",
    ".zip",
}

# Directory names to skip entirely (fast and predictable)
EXCLUDE_DIRNAMES = {
    "bin",
    "obj",
    ".git",
    "node_modules",
    "packages",
    ".vs",
}

# Noisy files we typically don't want in an AI-ingestion archive.
# (Matched case-insensitively via lowercasing before fnmatch.)
EXCLUDE_NAME_PATTERNS = {
    "*.user",
    "*.suo",
    "*.cache",
    "*.log",
    "*.tmp",
    "*.bak",
    "*.swp",
    "*.ds_store",
    "thumbs.db",
}


def should_skip_dir(path: Path) -> bool:
    """Return True if this directory should be excluded."""
    return path.name in EXCLUDE_DIRNAMES


def should_exclude_by_name(path: Path) -> bool:
    """Return True if file name matches any excluded noise pattern."""
    name = path.name.lower()
    for pat in EXCLUDE_NAME_PATTERNS:
        if fnmatch.fnmatch(name, pat):
            return True
    return False


def should_include_file(path: Path) -> bool:
    """
    Return True if this file should be included in the archive.

    Rules:
    - Exclude known binary/archive extensions.
    - Include only whitelisted extensions.
    - Exclude noisy IDE/user/cache/log files by name/pattern.
    - Exclude appsettings.*.json (except appsettings.json).
    """
    # Skip typical noise by name first (cheap)
    if should_exclude_by_name(path):
        return False

    suffix = path.suffix.lower()

    # Exclude known binaries/archives
    if suffix in EXCLUDE_EXTS:
        return False

    # Only include approved extensions
    if suffix not in INCLUDE_EXTS:
        return False

    name = path.name.lower()

    # Exclude environment-specific appsettings files:
    #   appsettings.Development.json, appsettings.Staging.json, etc.
    # Keep appsettings.json.
    if fnmatch.fnmatch(name, "appsettings.*.json") and name != "appsettings.json":
        return False

    return True


def iter_source_files(root: Path):
    """
    Efficiently iterate files under root, pruning excluded directories early.

    Uses an explicit stack to avoid recursion depth issues and minimize overhead.
    Skips symlinked directories to avoid accidental loops; includes symlinked files
    only if they are regular files as reported by Path.is_file().
    """
    stack = [root]

    while stack:
        current = stack.pop()
        try:
            for entry in current.iterdir():
                if entry.is_dir():
                    # Avoid walking symlinked directories (can create loops)
                    if entry.is_symlink():
                        continue
                    if should_skip_dir(entry):
                        continue
                    stack.append(entry)
                elif entry.is_file():
                    if should_include_file(entry):
                        yield entry
        except (PermissionError, FileNotFoundError):
            # PermissionError: can't read folder
            # FileNotFoundError: race conditions on changing trees
            continue


def create_zip(root: Path, out_zip: Path) -> int:
    """
    Create a zip at out_zip containing filtered source files under root.
    Returns the number of files added.
    """
    root = root.resolve()

    # Create output parent directory if needed
    if out_zip.parent and not out_zip.parent.exists():
        out_zip.parent.mkdir(parents=True, exist_ok=True)

    file_count = 0

    # Compression: deflate is widely supported and good for text files
    with zipfile.ZipFile(out_zip, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for file_path in iter_source_files(root):
            # Preserve structure by storing relative path inside the zip
            arcname = file_path.relative_to(root).as_posix()
            zf.write(file_path, arcname)
            file_count += 1

    return file_count


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        prog = Path(argv[0]).name
        print(
            f"Usage:\n  {prog} <input_directory> <output_zip_filename>\n\n"
            f"Example:\n  {prog} cs cs_sources.zip",
            file=sys.stderr,
        )
        return 2

    in_dir = Path(argv[1]).expanduser()
    out_zip = Path(argv[2]).expanduser()

    if not in_dir.exists() or not in_dir.is_dir():
        print(f"Error: input directory does not exist or is not a directory: {in_dir}", file=sys.stderr)
        return 2

    # Warn (not fatal) if output zip is inside the input tree, which can confuse reruns
    try:
        out_zip_resolved = out_zip.resolve()
        in_dir_resolved = in_dir.resolve()
        if str(out_zip_resolved).startswith(str(in_dir_resolved) + "/"):
            print(
                "Warning: output zip is inside the input directory. "
                "Consider writing it outside to avoid including it on later runs.",
                file=sys.stderr,
            )
    except Exception:
        pass

    count = create_zip(in_dir, out_zip)
    print(f"Created: {out_zip}  (files added: {count})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))