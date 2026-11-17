"""Durable state transitions. One atomic document holds success history AND the maintenance barrier."""
import copy
import datetime
import json
import os
import re
import tempfile
from pathlib import Path
from release import SafetyError, require


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix="." + path.name, dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as output:
            json.dump(value, output, sort_keys=True, indent=2)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
        if os.name != "nt":
            directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(directory)
            finally:
                os.close(directory)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


class ReleaseState:
    def __init__(self, path):
        self.path = Path(path)
        if self.path.exists():
            try:
                self.value = json.loads(self.path.read_text(encoding="utf-8"))
                require(self.value.get("format") == 1 and isinstance(self.value["history"], list) and
                        "current" in self.value and "previous" in self.value and "inflight" in self.value,
                        "Invalid state document")
            except (ValueError, KeyError, TypeError):
                raise SafetyError("Corrupt deployment state; preserve it and recover explicitly") from None
        else:
            self.value = {"format": 1, "current": None, "previous": None, "history": [], "inflight": None}

    def save(self):
        atomic_json(self.path, self.value)

    def require_idle(self):
        require(self.value["inflight"] is None,
                "Persistent maintenance barrier exists. Use recover.sh after investigation; never delete state to bypass it")

    def begin(self, kind, target=None, reason=None, recovery=False):
        if recovery:
            require(self.value["inflight"] is not None and reason and len(reason.strip()) >= 12,
                    "Recovery requires an existing barrier and a meaningful non-secret reason")
            prior = copy.deepcopy(self.value["inflight"])
        else:
            self.require_idle()
            prior = None
        self.value["inflight"] = {"kind": kind, "target": target, "stage": "prepared", "started_at": now(),
                                  "reason": reason, "prior": prior}
        self.save()  # Must be durable BEFORE stopping services, changing schema, or importing data.

    def stage(self, stage, **details):
        require(self.value["inflight"] is not None, "No active operation")
        self.value["inflight"].update(stage=stage, **details)
        self.save()

    def commit(self, release, schema_hash):
        require(self.value["inflight"] is not None and self.value["inflight"]["stage"] == "application-verified",
                "Cannot record success before schema and running-application verification")
        require((self.value["inflight"].get("target") or {}).get("manifest_digest") == release["manifest_digest"] and
                re.fullmatch(r"[0-9a-f]{64}", schema_hash) is not None, "Verified operation target/schema identity mismatch")
        current = self.value["current"]
        if current and current["manifest_digest"] != release["manifest_digest"]:
            # Same source can never be remapped, even if a remote immutable-release policy was changed.
            require(current["manifest"]["source_sha"] != release["manifest"]["source_sha"], "Source mapping changed")
            self.value["previous"] = current
        for old in self.value["history"]:
            if old["release"]["manifest"]["source_sha"] == release["manifest"]["source_sha"]:
                require(old["release"]["manifest_digest"] == release["manifest_digest"], "Historical source mapping changed")
        self.value["history"].append({"at": now(), "release": release, "schema_hash": schema_hash,
                                      "operation": self.value["inflight"]})
        self.value["current"] = release
        self.value["inflight"] = None
        self.save()  # Atomic current/previous/history/barrier transition, including on repeated deployment.

    def rollback_target(self):
        self.require_idle()
        require(self.value["previous"] is not None, "No previous distinct successful release")
        return self.value["previous"]
