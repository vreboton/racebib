"""Runner-side SSM client. Transmits parameters, never remote shell text or secrets."""
import json
import os
import re
import subprocess
import time


def aws(*args):
    environment = os.environ.copy()
    # SendCommand has no client idempotency token. A lost response must never
    # trigger SDK/CLI retries that could execute the deployment twice.
    environment["AWS_MAX_ATTEMPTS"] = "1"
    result = subprocess.run(["aws", "ssm", *args, "--output", "json", "--no-cli-pager"],
                            capture_output=True, text=True, timeout=45, env=environment)
    if result.returncode:
        if "InvocationDoesNotExist" in result.stderr and args[0] == "get-command-invocation":
            return None
        raise RuntimeError("SSM API failed; inspect AWS audit records and host state before retrying")
    return json.loads(result.stdout)


def parameters(source, tag):
    if not re.fullmatch(r"[0-9a-f]{40}", source):
        raise ValueError("Full lowercase source SHA required")
    if tag and not re.fullmatch(r"racebib-" + source + r"-recovery-[1-9][0-9]*-[1-9][0-9]*", tag):
        raise ValueError("Recovery tag/source mismatch")
    return {"SourceSha": [source], "ReleaseTag": [tag]}


def wait(command_id, instance, *, client=aws, sleep=time.sleep, clock=time.monotonic):
    deadline = clock() + 3900
    while clock() < deadline:
        value = client("get-command-invocation", "--command-id", command_id, "--instance-id", instance)
        if value is not None:
            status = value["Status"]
            if status == "Success":
                if value.get("ResponseCode") != 0:
                    raise RuntimeError("SSM success had a nonzero response code")
                return value
            if status not in {"Pending", "InProgress", "Delayed", "Cancelling"}:
                raise RuntimeError("SSM deployment ended: " + status + "; inspect host maintenance state")
        sleep(10)
    raise RuntimeError("SSM observation timed out; remote outcome unknown. Inspect command and host before retrying")


def main():
    values = parameters(os.environ["SOURCE"], os.environ.get("RELEASE_TAG", ""))
    instance = os.environ["INSTANCE_ID"]
    if not re.fullmatch(r"i-[0-9a-f]{17}", instance):
        raise ValueError("A single EC2 instance ID is required")
    document_hash = os.environ.get("DOCUMENT_SHA256", "")
    if not re.fullmatch(r"[0-9a-f]{64}", document_hash):
        raise ValueError("An independently approved SSM document SHA256 is required")
    result = aws("send-command", "--instance-ids", instance,
                 "--document-name", os.environ["DOCUMENT_NAME"], "--document-version", "1",
                 "--document-hash", document_hash, "--document-hash-type", "Sha256",
                 "--timeout-seconds", "120", "--parameters", json.dumps(values),
                 "--comment", "RaceBib approved immutable release deployment")
    command_id = result["Command"]["CommandId"]
    print("SSM command ID:", command_id, flush=True)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
            stream.write("SSM command ID: `" + command_id + "`\n")
    # Raw remote stdout/stderr may contain operational secrets; never print it.
    result = wait(command_id, instance)
    output("remote_command", "succeeded")
    verify_receipt(result.get("StandardOutputContent", ""), os.environ["SOURCE"])
    output("host_state", "verified")
    print("Remote deployment succeeded; host release state verified. External HTTPS availability is a separate check.")


def output(key, value):
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
            stream.write(key + "=" + value + "\n")


def verify_receipt(stdout, source):
    receipts = [line[len("RACEBIB_RESULT "):] for line in stdout.splitlines() if line.startswith("RACEBIB_RESULT ")]
    if len(receipts) != 1:
        raise RuntimeError("Remote command completed but host release-state receipt is missing or ambiguous")
    value = json.loads(receipts[0])
    if value != {"deployment": "succeeded", "host_state": "verified", "source_sha": source}:
        raise RuntimeError("Remote command completed but target host release state was not verified")


if __name__ == "__main__":
    main()
