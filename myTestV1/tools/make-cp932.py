#!/usr/bin/env python3
"""Writes the fixture's CP932 source file from its UTF-8 original (05 §3.4).

The fixture needs one file that is genuinely not UTF-8, because the extractor's encoding fallback
cannot be tested with a file that decodes either way. Keeping the readable original in
``tools/KEY01.src.txt`` and generating the CP932 copy means the content stays reviewable in a normal
editor while the fixture still holds real CP932 bytes.

Run from the repository root:

    python tools/make-cp932.py
"""
import io
import os
import sys

SOURCE = os.path.join("tools", "KEY01.src.txt")
TARGET = os.path.join("tests", "fixtures", "MiniPos", "Assets", "Common", "Legacy", "KEY01_KeyCalculator.cs")
ENCODING = "cp932"


def main() -> int:
    if not os.path.exists(SOURCE):
        print("source not found: {0}".format(SOURCE), file=sys.stderr)
        print("run this script from the repository root (the directory holding ScreenFlow.sln).", file=sys.stderr)
        return 1

    text = io.open(SOURCE, encoding="utf-8").read()

    try:
        encoded = text.encode(ENCODING)
    except UnicodeEncodeError as error:
        # A character the fixture cannot actually be saved as would make the file UTF-8 after all,
        # so this fails loudly rather than substituting a replacement character.
        bad = text[error.start:error.end]
        print(
            "{0} contains {1!r} (offset {2}), which {3} cannot represent.".format(
                SOURCE, bad, error.start, ENCODING),
            file=sys.stderr)
        return 2

    os.makedirs(os.path.dirname(TARGET), exist_ok=True)
    with io.open(TARGET, "wb") as handle:
        handle.write(encoded)

    # Proves the result is not valid UTF-8, which is the property the test asserts.
    try:
        encoded.decode("utf-8")
        print("{0} still decodes as UTF-8; the fixture needs a non-UTF-8 file.".format(TARGET), file=sys.stderr)
        return 3
    except UnicodeDecodeError:
        pass

    print("wrote {0} ({1} bytes, {2}, not valid UTF-8)".format(TARGET, len(encoded), ENCODING))
    return 0


if __name__ == "__main__":
    sys.exit(main())
