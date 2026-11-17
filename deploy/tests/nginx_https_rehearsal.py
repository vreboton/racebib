"""Disposable local/CI Nginx rehearsal: no host ports, production files, network or database."""
import argparse
from pathlib import Path
import subprocess
import tempfile
import time
import uuid

IMAGE = "nginx:stable-alpine@sha256:901e593c95dc85f5a729f5765516321366ef4ac401821431d408897e95287d0a"
DOMAIN = "racebib.example.invalid"


def run(*command):
    return subprocess.run(command, check=True, capture_output=True, text=True, timeout=90).stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--openssl", default="openssl")
    args = parser.parse_args()
    name = "racebib-nginx-rehearsal-" + uuid.uuid4().hex[:12]
    with tempfile.TemporaryDirectory(prefix="racebib-nginx-") as temporary:
        root = Path(temporary)
        cert = root / "cert"
        cert.mkdir()
        challenge = root / "webroot/.well-known/acme-challenge"
        challenge.mkdir(parents=True)
        (challenge / "test-token").write_text("disposable-acme-response")
        run(args.openssl, "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
            "-subj", "/CN=" + DOMAIN, "-addext", "subjectAltName=DNS:" + DOMAIN,
            "-keyout", str(cert / "privkey.pem"), "-out", str(cert / "fullchain.pem"))
        template = Path(__file__).resolve().parents[1] / "nginx/racebib-https.conf.example"
        config = root / "site.conf"
        # Echo request/proxy headers from a synthetic loopback web service, never an application.
        config.write_text(template.read_text().replace("__DOMAIN__", DOMAIN) + '''
server {
    listen 127.0.0.1:8080;
    location / { return 200 "$request_uri|$host|$http_x_forwarded_proto"; }
}
''')
        try:
            run("docker", "run", "--detach", "--name", name, "--network", "none", "--read-only",
                "--memory", "128m", "--memory-swap", "128m", "--tmpfs", "/var/cache/nginx", "--tmpfs", "/var/run",
                "--mount", f"type=bind,source={config},target=/etc/nginx/conf.d/default.conf,readonly",
                "--mount", f"type=bind,source={cert},target=/etc/letsencrypt/live/{DOMAIN},readonly",
                "--mount", f"type=bind,source={root / 'webroot'},target=/var/www/letsencrypt,readonly",
                "--entrypoint", "nginx", IMAGE, "-g", "daemon off;")
            run("docker", "exec", name, "nginx", "-t")

            def request(path, *, tls=False, post=False, host=DOMAIN):
                command = ["docker", "exec", name, "curl", "--silent", "--show-error", "--max-time", "5",
                           "--noproxy", "*", "--include", "--header", "Host: " + host]
                if tls:
                    command += ["--cacert", f"/etc/letsencrypt/live/{DOMAIN}/fullchain.pem",
                                "--resolve", DOMAIN + ":443:127.0.0.1"]
                if post:
                    command += ["--data", "synthetic-test-body"]
                return run(*(command + [("https://" + DOMAIN if tls else "http://127.0.0.1") + path]))

            for attempt in range(30):
                try:
                    response = request("/.well-known/acme-challenge/test-token")
                    break
                except subprocess.CalledProcessError:
                    if attempt == 29:
                        raise
                    time.sleep(0.1)
            assert response.startswith("HTTP/1.1 200 ") and response.endswith("disposable-acme-response"), response
            assert "Content-Type: text/plain" in response and "Location:" not in response, response
            missing = request("/.well-known/acme-challenge/missing")
            assert missing.startswith("HTTP/1.1 404 ") and "Location:" not in missing, missing
            for path, post, host in (("/", False, DOMAIN), ("/api/v1/auth/login?next=%2F", True, DOMAIN),
                                     ("/.well-known/acme-challenge-other", False, DOMAIN), ("/login", False, "untrusted.invalid")):
                response = request(path, post=post, host=host)
                assert response.startswith("HTTP/1.1 308 "), response
                assert "Location: https://" + DOMAIN + path + "\n" in response, response
            for path in ("/health", "/api/v1/events?test=1", "/client/route"):
                response = request(path, tls=True)
                assert response.startswith("HTTP/1.1 200 ") and response.endswith(path + "|" + DOMAIN + "|https"), response
                for header in ("X-Content-Type-Options: nosniff", "X-Frame-Options: DENY",
                               "Referrer-Policy: strict-origin-when-cross-origin"):
                    assert header in response, response
            print("Nginx rehearsal passed: ACME 200/404, HTTP 308, canonical redirect, trusted TLS, proxy paths/headers and security headers.")
        finally:
            subprocess.run(["docker", "rm", "--force", name], capture_output=True, timeout=30, check=False)


if __name__ == "__main__":
    main()
