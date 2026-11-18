"""Strict release selector validation for local preflight and the SSH entry."""
import re


def parameters(source, tag):
    if not re.fullmatch(r"[0-9a-f]{40}", source):
        raise ValueError("Full lowercase source SHA required")
    if tag and not re.fullmatch(r"racebib-" + source + r"-recovery-[1-9][0-9]*-[1-9][0-9]*", tag):
        raise ValueError("Recovery tag/source mismatch")
    return {"SourceSha": [source], "ReleaseTag": [tag]}
