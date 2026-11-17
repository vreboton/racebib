from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from operations import Operations
from release import SafetyError


class HttpsConfigurationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        (self.root / "nginx").mkdir()
        template = Path(__file__).resolve().parents[1] / "nginx/racebib-https.conf.example"
        shutil.copyfile(template, self.root / "nginx/racebib-https.conf.example")
        self.active = self.root / "active.conf"
        self.active.write_text(template.read_text().replace("__DOMAIN__", "racebib.example.invalid"))
        self.runner = Mock()
        self.ops = Operations(self.root, runner=self.runner)
        self.path = patch("operations.Path", side_effect=lambda value: self.active if str(value) ==
                          "/etc/nginx/sites-enabled/racebib" else Path(value))
        self.path.start()
        self.addCleanup(self.path.stop)
        self.connection = patch("operations.socket.create_connection")
        self.socket = self.connection.start()
        self.addCleanup(self.connection.stop)
        self.context = patch("operations.ssl.create_default_context")
        self.tls = self.context.start()
        self.addCleanup(self.context.stop)

    def test_reviewed_acme_template_passes_exact_validation_and_verified_tls(self):
        self.ops.access_configuration("https://racebib.example.invalid", "https")
        self.assertEqual(self.ops.public_url, "https://racebib.example.invalid")
        self.assertEqual(self.runner.call_args_list[0].args[0], ["nginx", "-t"])
        self.runner.assert_called_with(["systemctl", "reload", "nginx"])
        self.socket.assert_called_once_with(("racebib.example.invalid", 443), timeout=15)
        self.tls.assert_called_once_with()
        self.tls.return_value.wrap_socket.assert_called_once_with(
            self.socket.return_value.__enter__.return_value, server_hostname="racebib.example.invalid")

    def test_modified_or_wrong_domain_template_fails_before_reload_or_tls(self):
        original = self.active.read_text()
        for text in (original + "\n", original.replace("racebib.example.invalid", "other.example.invalid"),
                     original.replace("try_files $uri =404;", "proxy_pass http://127.0.0.1:8080;")):
            self.active.write_text(text)
            self.runner.reset_mock()
            with self.assertRaisesRegex(SafetyError, "reviewed TLS template"):
                self.ops.access_configuration("https://racebib.example.invalid", "https")
            self.runner.assert_called_once_with(["nginx", "-t"])
            self.socket.assert_not_called()

    def test_http_origin_or_wrong_mode_cannot_enable_normal_access(self):
        for base, mode in (("http://racebib.example.invalid", "https"),
                           ("https://racebib.example.invalid/path", "https"),
                           ("https://racebib.example.invalid", "unrestricted-http")):
            with self.subTest(base=base, mode=mode), self.assertRaises(SafetyError):
                self.ops.access_configuration(base, mode)
        self.socket.assert_not_called()

    def test_certificate_or_hostname_failure_cannot_mark_https_ready(self):
        self.tls.return_value.wrap_socket.side_effect = OSError("test certificate rejected")
        with self.assertRaisesRegex(SafetyError, "Verified public HTTPS is unavailable"):
            self.ops.access_configuration("https://racebib.example.invalid", "https")
        self.assertFalse(hasattr(self.ops, "public_url"))

    def test_nginx_validation_failure_cannot_reload_or_mark_https_ready(self):
        self.runner.side_effect = SafetyError("test invalid nginx configuration")
        with self.assertRaises(SafetyError):
            self.ops.access_configuration("https://racebib.example.invalid", "https")
        self.runner.assert_called_once_with(["nginx", "-t"])
        self.socket.assert_not_called()


if __name__ == "__main__":
    unittest.main()
