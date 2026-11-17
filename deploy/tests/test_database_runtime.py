import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from operations import Operations
from release import SafetyError
from release_state import ReleaseState


class DatabaseRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / "state").mkdir()
        self.config = {"services": {"mysql": {"image": "mysql:8.4@sha256:" + "a" * 64,
                       "environment": {"MYSQL_DATABASE": "racebib"}, "command": ["--max-connections=30"],
                       "mem_limit": "469762048", "memswap_limit": "805306368"}},
                       "networks": {"database": {"name": "racebib-production_database"}},
                       "volumes": {"mysql_data": {"name": "racebib-production_mysql_data"}}}
        mysql = self.config["services"]["mysql"]
        self.container = {"Config": {"Image": mysql["image"], "Env": ["MYSQL_DATABASE=racebib"], "Cmd": mysql["command"]},
                          "HostConfig": {"PortBindings": {}, "RestartPolicy": {"Name": "unless-stopped"},
                                         "Memory": 469762048, "MemorySwap": 805306368},
                          "NetworkSettings": {"Networks": {"racebib-production_database": {}}},
                          "Mounts": [{"Name": "racebib-production_mysql_data", "Destination": "/var/lib/mysql"}],
                          "State": {"Running": True, "Health": {"Status": "healthy"}}}
        self.volume_exists = True
        self.calls = []
        self.ops = Operations(self.root, runner=self.run_command)
        self.ops.dc = Mock(side_effect=self.compose)
        self.ops.db_configuration = Mock(return_value=self.config)
        self.ops.no_oneoff = Mock()

    def run_command(self, command, **kwargs):
        self.calls.append(command)
        if command[:3] == ["docker", "container", "inspect"]:
            return json.dumps([self.container]).encode()
        if command[:3] == ["docker", "network", "inspect"]:
            return b'[{"Internal":true}]'
        if command[:3] == ["docker", "volume", "ls"]:
            return b"racebib-production_mysql_data\n" if self.volume_exists else b""
        raise AssertionError(command)

    def compose(self, *arguments, **kwargs):
        self.calls.append(list(arguments))
        self.assertTrue(kwargs["database"])
        if arguments == ("ps", "--all", "--quiet", "mysql"):
            return b"existing-mysql\n"
        if arguments[0] in ("start", "pull", "up"):
            return b""
        raise AssertionError(arguments)

    def test_equal_byte_counts_accept_inspect_integers_and_compose_strings(self):
        for inspect_strings, compose_strings in ((False, True), (True, False), (False, False), (True, True)):
            for actual, expected in (("Memory", "mem_limit"), ("MemorySwap", "memswap_limit")):
                value = int(self.config["services"]["mysql"][expected])
                self.container["HostConfig"][actual] = str(value) if inspect_strings else value
                self.config["services"]["mysql"][expected] = str(value) if compose_strings else value
            self.ops.db_runtime(self.config)

    def test_actual_mismatches_still_fail_for_both_limits(self):
        for field in ("Memory", "MemorySwap"):
            original = self.container["HostConfig"][field]
            self.container["HostConfig"][field] = original + 1
            with self.subTest(field=field), self.assertRaisesRegex(SafetyError, "resource limits differ"):
                self.ops.db_runtime(self.config)
            self.container["HostConfig"][field] = original

    def test_invalid_byte_counts_fail_on_both_sides_of_each_limit(self):
        invalid = (None, True, False, 0, -1, 1.5, 469762048.0, "", "0", "-1", "448m", "1.0", "1e6",
                   " 469762048", "469762048\n", "+1", "１２３", [], {}, 2**63, str(2**63), "9" * 100)
        for side, field in ((self.container["HostConfig"], "Memory"), (self.container["HostConfig"], "MemorySwap"),
                            (self.config["services"]["mysql"], "mem_limit"), (self.config["services"]["mysql"], "memswap_limit")):
            original = side[field]
            for value in invalid:
                side[field] = value
                with self.subTest(field=field, value=value), self.assertRaisesRegex(SafetyError, "Invalid MySQL resource limit"):
                    self.ops.db_runtime(self.config)
            side[field] = original

    def test_nearby_image_environment_command_network_volume_and_health_checks_remain_strict(self):
        original = copy.deepcopy(self.container)
        for key, value in (("image", "mysql:8.4"), ("environment", ["MYSQL_DATABASE=other"]), ("command", []),
                           ("ports", {"3306/tcp": []}), ("network", {"other": {}}), ("volume", []),
                           ("restart", "always"), ("health", {"Status": "unhealthy"})):
            self.container = copy.deepcopy(original)
            if key in ("image", "environment", "command"):
                self.container["Config"][{"image": "Image", "environment": "Env", "command": "Cmd"}[key]] = value
            elif key == "ports":
                self.container["HostConfig"]["PortBindings"] = value
            elif key == "restart":
                self.container["HostConfig"]["RestartPolicy"]["Name"] = value
            elif key == "network":
                self.container["NetworkSettings"]["Networks"] = value
            elif key == "volume":
                self.container["Mounts"] = value
            else:
                self.container["State"]["Health"] = value
            with self.subTest(check=key), self.assertRaises(SafetyError):
                self.ops.db_runtime(self.config)

    def test_failed_initialization_resumes_by_adopting_without_pull_or_recreation(self):
        self.volume_exists = False
        self.container["HostConfig"]["Memory"] += 1
        with self.assertRaises(SafetyError):
            self.ops.initialize_database()
        self.assertFalse((self.root / "state/database.json").exists())
        prior = copy.deepcopy(self.ops.state.value["inflight"])
        self.container["HostConfig"]["Memory"] -= 1
        self.volume_exists = True
        self.calls.clear()
        self.ops.state = ReleaseState(self.root / "state/release-state.json")
        self.ops.initialize_database(adopt=True, resume=True, reason="Reviewed matching existing initialization container")
        self.assertTrue((self.root / "state/database.json").is_file())
        state = ReleaseState(self.root / "state/release-state.json").value
        self.assertEqual(state["inflight"]["prior"], prior)
        self.assertEqual(state["inflight"]["stage"], "database-initialized")
        self.assertIsNone(state["current"])
        self.assertEqual([c[0] for c in self.calls if c[0] in ("start", "pull", "up")], ["start"])

    def test_resume_rejects_other_barriers_missing_approval_or_existing_baseline(self):
        for kind, stage, adopt, reason in (("migration", "prepared", True, "Reviewed existing initialization"),
                                           ("database-initialization", "database-initialized", True, "Reviewed initialization"),
                                           ("database-initialization", "prepared", False, "Reviewed initialization"),
                                           ("database-initialization", "prepared", True, "short")):
            self.ops.state.value["inflight"] = {"kind": kind, "stage": stage, "target": None}
            self.calls.clear()
            with self.subTest(kind=kind, stage=stage, adopt=adopt), self.assertRaises(SafetyError):
                self.ops.initialize_database(adopt=adopt, resume=True, reason=reason)
            self.assertEqual(self.calls, [])
        self.ops.state.value["inflight"] = {"kind": "database-initialization", "stage": "prepared", "target": None}
        (self.root / "state/database.json").write_text("{}")
        with self.assertRaisesRegex(SafetyError, "already initialized"):
            self.ops.initialize_database(adopt=True, resume=True, reason="Reviewed existing initialization")

    def test_failed_adoption_keeps_barrier_and_does_not_record_baseline(self):
        self.ops.state.begin("database-initialization")
        self.container["HostConfig"]["MemorySwap"] += 1
        with self.assertRaises(SafetyError):
            self.ops.initialize_database(adopt=True, resume=True, reason="Reviewed existing initialization")
        self.assertFalse((self.root / "state/database.json").exists())
        self.assertEqual(ReleaseState(self.root / "state/release-state.json").value["inflight"]["kind"], "database-initialization")
        self.assertFalse(any(c[0] in ("start", "pull", "up") for c in self.calls))

    def test_resume_never_bypasses_application_history_or_ordinary_initialization_barrier(self):
        self.ops.state.begin("database-initialization")
        for field, value in (("current", {"release_id": 1}), ("previous", {"release_id": 1}), ("history", [{"release_id": 1}])):
            original = self.ops.state.value[field]
            self.ops.state.value[field] = value
            with self.subTest(field=field), self.assertRaises(SafetyError):
                self.ops.initialize_database(adopt=True, resume=True, reason="Reviewed existing initialization")
            self.ops.state.value[field] = original
        with self.assertRaisesRegex(SafetyError, "maintenance barrier"):
            self.ops.initialize_database(adopt=True)
        self.assertEqual(self.calls, [])


if __name__ == "__main__":
    unittest.main()
