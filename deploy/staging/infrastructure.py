"""New-stack-only CloudFormation rehearsal. No production discovery or mutation."""
import argparse
import base64
import hashlib
import gzip
import ipaddress
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid

REGION = "ap-southeast-2"
PRODUCTION_IP = "15.135.108.24"
PRODUCTION_DOMAIN = "racebib.vreboton.com"
TYPES = {"AWS::EC2::VPC", "AWS::EC2::Subnet", "AWS::EC2::InternetGateway",
         "AWS::EC2::VPCGatewayAttachment", "AWS::EC2::RouteTable", "AWS::EC2::Route",
         "AWS::EC2::SubnetRouteTableAssociation", "AWS::EC2::SecurityGroup", "AWS::EC2::Instance"}


class NotReady(RuntimeError):
    pass


ERROR_CODES = {"AccessDenied", "AccessDeniedException", "UnauthorizedOperation", "ExpiredToken", "ExpiredTokenException",
               "InvalidClientTokenId", "RequestExpired", "ValidationError", "InvalidParameterValue", "Throttling",
               "ThrottlingException", "RequestLimitExceeded", "InvalidInstanceID.NotFound",
               "InvalidNetworkInterfaceID.NotFound", "InvalidVolume.NotFound"}
ACTIONS = {"plan", "create", "inspect", "trust", "destroy", "cleanup"}
OPERATIONS = {"get-caller-identity", "describe-images", "create-stack", "describe-stacks", "get-template",
              "list-stack-resources", "describe-stack-events", "describe-vpcs", "describe-subnets",
              "describe-security-groups", "describe-route-tables", "describe-internet-gateways", "describe-volumes",
              "describe-network-interfaces", "describe-instances", "get-console-output", "delete-stack", "wait",
              "describe-addresses", "describe-nat-gateways", "describe-snapshots"}


class InfrastructureFailure(RuntimeError):
    def __init__(self, category, service="none", operation="none", waiter="none", exit_code="none", aws_error_code="none"):
        super().__init__("Staging infrastructure failure; raw output withheld")
        self.fields = dict(category=category, service=service, operation=operation, waiter=waiter,
                           exit_code=str(exit_code), aws_error_code=aws_error_code)


def diagnostic(error, action):
    """Only fixed vocabulary and source line numbers; never exception text, paths, arguments or AWS output."""
    value = dict(protocol="racebib-stage-error-v1", action=action if action in ACTIONS else "unknown",
                 category="controller", service="none", operation="none", waiter="none", exit_code="none",
                 aws_error_code="none", exception_type="unknown", phase="main", line="0")
    if isinstance(error, InfrastructureFailure):
        value.update(error.fields)
    if type(error).__name__ in {"InfrastructureFailure", "RuntimeError", "KeyError", "TypeError", "ValueError",
                                "AttributeError", "JSONDecodeError", "OSError", "FileNotFoundError", "PermissionError", "NotReady"}:
        value["exception_type"] = type(error).__name__
    trace = error.__traceback__
    phases = {"main", "aws", "validate_record", "account_guard", "create", "destroy", "cleanup", "save",
              "stack_guard", "validate_resources", "inspect", "trust", "ownership", "unprotected", "observed_stack"}
    while trace:
        if trace.tb_frame.f_code.co_filename == __file__ and trace.tb_frame.f_code.co_name in phases:
            value.update(phase=trace.tb_frame.f_code.co_name, line=str(trace.tb_lineno))
        trace = trace.tb_next
    return value


def require(value, message):
    if not value:
        raise RuntimeError(message)


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def aws(record, service, *arguments, missing_ok=False):
    command = [record["aws_cli"], "--region", REGION, "--profile", record["profile"], service, *arguments,
               "--output", "json", "--no-cli-pager"]
    environment = __import__("os").environ.copy()
    for name in list(environment):
        if name.upper().startswith(('GH', 'RACEBIB_STAGING_')):
            environment.pop(name)
    environment["AWS_MAX_ATTEMPTS"] = "1"  # Never duplicate provisioning/deletion after a lost response.
    operation = arguments[0] if arguments[0] in OPERATIONS else "none"
    safe_service = service if service in {"sts", "ec2", "cloudformation"} else "none"
    waiter = arguments[1] if operation == "wait" and arguments[1] in {"stack-create-complete", "stack-delete-complete"} else "none"
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=1200 if operation == "wait" else 120, env=environment)
    except subprocess.TimeoutExpired:
        raise InfrastructureFailure("aws-timeout", safe_service, operation, waiter) from None
    except OSError:
        raise InfrastructureFailure("aws-start", safe_service, operation, waiter) from None
    except UnicodeError:
        raise InfrastructureFailure("aws-encoding", safe_service, operation, waiter) from None
    if result.returncode:
        match = re.search(r"An error occurred \(([A-Za-z0-9.]+)\) when calling", result.stderr)
        code = match[1] if match and match[1] in ERROR_CODES else "unknown"
        selector = arguments[arguments.index("--stack-name") + 1] if "--stack-name" in arguments else ""
        missing_stack = service == 'cloudformation' and operation == 'describe-stacks' and code == 'ValidationError' and bool(
            re.search(r"Stack with id " + re.escape(selector) + r" does not exist\s*$", result.stderr))
        missing_resource = {"describe-instances": "InvalidInstanceID.NotFound",
                            "describe-network-interfaces": "InvalidNetworkInterfaceID.NotFound", "describe-volumes": "InvalidVolume.NotFound"}
        if missing_ok and (missing_stack or code == missing_resource.get(operation)):
            return None
        raise InfrastructureFailure("aws-cli", safe_service, operation, waiter, result.returncode, code)
    try:
        value = json.loads(result.stdout) if result.stdout.strip() else {}
        if not isinstance(value, dict):
            raise ValueError()
        return value
    except ValueError:
        raise InfrastructureFailure("aws-json", safe_service, operation, waiter, result.returncode) from None


def validate_record(record):
    require(record["format"] == 1 and re.fullmatch(r"[0-9a-f]{32}", record["run_id"]), "Invalid staging run identity")
    require(record["stack_name"] == "racebib-staging-" + record["run_id"], "Staging stack name mismatch")
    require(re.fullmatch(r"[0-9]{12}", record["account"]), "Expected AWS account is mandatory")
    require(re.fullmatch(r"[A-Za-z0-9_-]+", record["profile"]), "Use a named short-lived AWS profile")
    require(record["region"] == REGION, "Region is fixed to the isolated rehearsal region")
    network = ipaddress.ip_network(record["operator_cidr"], strict=True)
    require(network.version == 4 and network.prefixlen == 32 and network.network_address.is_global and
            str(network.network_address) != PRODUCTION_IP, "Use the trusted PC's public IPv4 /32, never production or broad ingress")
    require(30 <= record["ttl_minutes"] <= 180, "Rehearsal TTL must be 30..180 minutes")
    require(re.fullmatch(r"ami-[0-9a-f]{17}", record["ami"]), "Use a reviewed explicit Canonical Ubuntu AMI")
    protected = record.get('protected_ids', [])
    require(isinstance(protected, list) and all(isinstance(x, str) and re.fullmatch(r'(?:i|vpc|subnet|sg|igw|rtb|vol|eni)-[0-9a-f]{8,17}', x) for x in protected) and
            any(x.startswith('i-') for x in protected) and any(x.startswith('vpc-') for x in protected),
            'Explicit protected production instance/VPC identifiers are required; include other protected resource IDs')
    require(record["template_digest"] == digest(record["template"]), "Local staging template was modified")
    require(set(resource["Type"] for resource in record["template"]["Resources"].values()) == TYPES, "Unexpected infrastructure resource type")
    resources = record["template"]["Resources"]
    require(set(resources) == {'Network','Subnet','Gateway','Attachment','Routes','DefaultRoute','SubnetRoutes','Firewall','Host'}, 'Unexpected logical resources')
    host = resources['Host']['Properties']
    require(host['SubnetId'] == {'Ref':'Subnet'} and host['SecurityGroupIds'] == [{'Ref':'Firewall'}] and
            host['ImageId'] == record['ami'] and host['InstanceType'] == 't3.small' and
            host['BlockDeviceMappings'][0]['Ebs'] == {'VolumeSize':30,'VolumeType':'gp3','Encrypted':True,'DeleteOnTermination':True}, 'Host isolation/storage changed')
    require(resources['Subnet']['Properties']['VpcId'] == resources['Firewall']['Properties']['VpcId'] == {'Ref':'Network'}, 'Existing VPC references forbidden')
    require(resources['Firewall']['Properties']['SecurityGroupIngress'] == [{'IpProtocol':'tcp','FromPort':p,'ToPort':p,'CidrIp':record['operator_cidr']} for p in (22,443)], 'Ingress differs from approved /32')
    require(all(r.get('DeletionPolicy','Delete') == 'Delete' for r in resources.values()), 'Retained resources forbidden')
    if record.get("stack_id"):
        prefix = f"arn:aws:cloudformation:{REGION}:{record['account']}:stack/{record['stack_name']}/"
        require(record["stack_id"].startswith(prefix), "Staging stack ARN/account mismatch")


def allowed_bundle(repo):
    paths = [repo / "compose.database.yml", repo / "compose.production.yml"]
    paths += sorted((repo / "deploy/scripts").glob("*.py")) + sorted((repo / "deploy/scripts").glob("*.sh"))
    paths += sorted((repo / "deploy/nginx").glob("*.conf")) + [repo / "deploy/nginx/racebib-https.conf.example"]
    return {path.relative_to(repo).as_posix(): base64.b64encode(path.read_text(encoding='utf-8').encode()).decode() for path in paths}


def make_template(run_id, cidr, ami, bootstrap_key, deploy_key, ttl, bundle_digest):
    for key in (bootstrap_key, deploy_key):
        require(re.fullmatch(r"ssh-ed25519 [A-Za-z0-9+/=]+(?: [A-Za-z0-9_-]+)?", key.strip()), "Fresh staging Ed25519 public keys required")
    require(bootstrap_key.split()[1] != deploy_key.split()[1], "Bootstrap and restricted deployment keys must be distinct")
    here = Path(__file__).resolve().parent
    cloud = (here / "bootstrap.sh").read_text()
    replacements = {"@@RUN@@": run_id, "@@CIDR@@": cidr, "@@BOOTSTRAP_KEY@@": bootstrap_key.strip(),
                    "@@DEPLOY_KEY@@": deploy_key.strip(), "@@TTL@@": str(ttl), "@@BUNDLE_DIGEST@@": bundle_digest,
                    "@@CONTROL@@": base64.b64encode((here / "host_control.py").read_text().encode()).decode()}
    for token, value in replacements.items():
        cloud = cloud.replace(token, value)
    tags = [{"Key": "Environment", "Value": "racebib-staging"}, {"Key": "StageRun", "Value": run_id},
            {"Key": "Temporary", "Value": "true"}, {"Key": "ManagedBy", "Value": "racebib-rehearsal"}]
    resources = {
        "Network": {"Type": "AWS::EC2::VPC", "Properties": {"CidrBlock": "10.245.0.0/24", "EnableDnsSupport": True, "EnableDnsHostnames": True, "Tags": tags}},
        "Subnet": {"Type": "AWS::EC2::Subnet", "Properties": {"VpcId": {"Ref": "Network"}, "CidrBlock": "10.245.0.0/26", "MapPublicIpOnLaunch": True, "Tags": tags}},
        "Gateway": {"Type": "AWS::EC2::InternetGateway", "Properties": {"Tags": tags}},
        "Attachment": {"Type": "AWS::EC2::VPCGatewayAttachment", "Properties": {"VpcId": {"Ref": "Network"}, "InternetGatewayId": {"Ref": "Gateway"}}},
        "Routes": {"Type": "AWS::EC2::RouteTable", "Properties": {"VpcId": {"Ref": "Network"}, "Tags": tags}},
        "DefaultRoute": {"Type": "AWS::EC2::Route", "DependsOn": "Attachment", "Properties": {"RouteTableId": {"Ref": "Routes"}, "DestinationCidrBlock": "0.0.0.0/0", "GatewayId": {"Ref": "Gateway"}}},
        "SubnetRoutes": {"Type": "AWS::EC2::SubnetRouteTableAssociation", "Properties": {"SubnetId": {"Ref": "Subnet"}, "RouteTableId": {"Ref": "Routes"}}},
        "Firewall": {"Type": "AWS::EC2::SecurityGroup", "Properties": {"GroupDescription": "Temporary RaceBib trusted-PC-only rehearsal", "VpcId": {"Ref": "Network"}, "Tags": tags,
            "SecurityGroupIngress": [{"IpProtocol": "tcp", "FromPort": port, "ToPort": port, "CidrIp": cidr} for port in (22, 443)],
            "SecurityGroupEgress": [{"IpProtocol": "tcp", "FromPort": 443, "ToPort": 443, "CidrIp": "0.0.0.0/0"}]}},
        "Host": {"Type": "AWS::EC2::Instance", "DependsOn": ["DefaultRoute", "SubnetRoutes"], "Properties": {
            "ImageId": ami, "InstanceType": "t3.small", "CreditSpecification": {"CPUCredits": "standard"},
            "SubnetId": {"Ref": "Subnet"}, "SecurityGroupIds": [{"Ref": "Firewall"}], "Tags": tags,
            "MetadataOptions": {"HttpTokens": "required", "HttpPutResponseHopLimit": 1},
            "InstanceInitiatedShutdownBehavior": "terminate", "PropagateTagsToVolumeOnCreation": True,
            "BlockDeviceMappings": [{"DeviceName": "/dev/sda1", "Ebs": {"VolumeSize": 30, "VolumeType": "gp3", "Encrypted": True, "DeleteOnTermination": True}}],
            "UserData": base64.b64encode(gzip.compress(cloud.encode(), mtime=0)).decode()}}
    }
    return {"AWSTemplateFormatVersion": "2010-09-09", "Description": "New isolated temporary RaceBib staging resources only", "Resources": resources}


def plan(directory, account, profile, cidr, ami, bootstrap_key, deploy_key, ttl=120, protected_ids=None):
    directory = Path(directory).resolve()
    require(not (directory / "run.json").exists(), "Never reuse an existing staging run directory")
    repo = Path(__file__).resolve().parents[2]
    require(not directory.is_relative_to(repo), "Staging state/keys belong outside the checkout")
    bundle = allowed_bundle(repo)
    run_id = uuid.uuid4().hex
    template = make_template(run_id, cidr, ami, bootstrap_key, deploy_key, ttl, digest(bundle))
    require(len(json.dumps(template).encode()) < 51200, "CloudFormation template exceeds inline limit")
    # Conservative model, not a bill cap: Sydney t3.small + IPv4 + 30 GiB gp3.
    estimate = round((ttl / 60) * (0.0264 + 0.005 + 30 * 0.096 / 730) + 0.25, 2)
    record = {"format": 1, "run_id": run_id, "stack_name": "racebib-staging-" + run_id, "account": account,
              "region": REGION, "profile": profile, "operator_cidr": cidr, "ami": ami, "ttl_minutes": ttl,
              "estimate_usd": estimate, "template": template, "template_digest": digest(template), "bundle_digest": digest(bundle),
              'protected_ids': protected_ids}
    validate_record(record)
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "bundle.json").write_text(json.dumps(bundle), encoding="utf-8")
    (directory / "template.json").write_text(json.dumps(template), encoding="utf-8")
    save(directory, record)
    return record


def save(directory, record):
    path = Path(directory) / "run.json"
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(record), encoding="utf-8")
    temporary.replace(path)


def account_guard(record):
    validate_record(record)
    require(aws(record, "sts", "get-caller-identity")["Account"] == record["account"], "AWS account mismatch")


def create(directory, record, approved_run, execution_id=None):
    require(approved_run == record["run_id"], "Explicit approval of the displayed cost/run is required")
    require(not record.get("stack_id"), "Never submit creation twice; inspect existing stack")
    require(not record.get("creation_submitted"), "Creation outcome uncertain; inspect by unique stack name")
    require(isinstance(execution_id, str) and re.fullmatch(r'[0-9a-f]{32}', execution_id), 'Current accepted execution identity required')
    claim = json.loads((Path(directory) / 'execution.json').read_text(encoding='utf-8-sig'))
    require(claim == {'format': 1, 'run_id': record['run_id'], 'execution_id': execution_id}, 'Execution authorization mismatch')
    # Atomic, durable consumption also blocks concurrent direct Python create calls with a stale run.json.
    with (Path(directory) / 'creation-attempt.json').open('x', encoding='utf-8') as attempt:
        json.dump(claim, attempt); attempt.flush(); os.fsync(attempt.fileno())
    account_guard(record)
    require(digest(json.loads((Path(directory) / 'template.json').read_text(encoding='utf-8-sig'))) == record['template_digest'], 'Reviewed template file was modified')
    images = aws(record, "ec2", "describe-images", "--image-ids", record["ami"], "--owners", "099720109477")["Images"]
    require(len(images) == 1 and images[0].get("State") == "available" and images[0]["Architecture"] == "x86_64" and
            images[0]["Name"].startswith("ubuntu/images/hvm-ssd-gp3/ubuntu-noble-24.04-amd64-server-"), "Reviewed Canonical Ubuntu 24.04 x86_64 AMI required")
    # Persist submission intent BEFORE the non-idempotent API call. Never auto-resubmit.
    require(not record.get("creation_submitted"), "Creation outcome uncertain; inspect by unique stack name")
    record["creation_submitted"] = True
    record["execution_id"] = execution_id
    save(directory, record)
    result = aws(record, "cloudformation", "create-stack", "--stack-name", record["stack_name"],
                 "--template-body", "file://" + str((Path(directory) / "template.json").resolve()),
                 "--resource-types", *sorted(TYPES),
                 "--tags", "Key=Environment,Value=racebib-staging", "Key=StageRun,Value=" + record["run_id"],
                 'Key=ManagedBy,Value=racebib-rehearsal', 'Key=Temporary,Value=true')
    record["stack_id"] = result["StackId"]
    save(directory, record)
    aws(record, "cloudformation", "wait", "stack-create-complete", "--stack-name", record["stack_id"])
    return inspect(record)


def ownership(record, value, logical=None):
    tags = {item['Key']: item['Value'] for item in value.get('Tags', [])}
    expected = {'Environment':'racebib-staging', 'StageRun':record['run_id'], 'ManagedBy':'racebib-rehearsal', 'Temporary':'true'}
    if logical:
        expected.update({'aws:cloudformation:stack-id':record['stack_id'], 'aws:cloudformation:logical-id':logical})
    require(all(tags.get(key) == item for key, item in expected.items()), 'Resource staging ownership cannot be established')


def unprotected(record, *values):
    require(all(value not in record['protected_ids'] and value != PRODUCTION_IP for value in values), 'Protected production identifier encountered')


def stack_guard(record, *, deleting=False):
    account_guard(record)
    require(record.get("creation_submitted"), "This tool did not submit the staging stack")
    # Recovery from a lost CreateStack response is read-only, by its unique nonce name.
    stack = aws(record, "cloudformation", "describe-stacks", "--stack-name", record.get("stack_id") or record["stack_name"])["Stacks"][0]
    ownership(record, stack)
    status = stack['StackStatus']
    require(status in {'CREATE_COMPLETE','CREATE_FAILED','ROLLBACK_COMPLETE','ROLLBACK_FAILED','DELETE_FAILED'},
            'Stack changed or is not quiescent; wait/read-only investigation required')
    if record.get("stack_id"):
        require(stack["StackId"] == record["stack_id"], "Stack identity changed")
    record["stack_id"] = stack["StackId"]
    validate_record(record)
    actual = aws(record, "cloudformation", "get-template", "--stack-name", record["stack_id"], "--template-stage", "Original")["TemplateBody"]
    actual = json.loads(actual) if isinstance(actual, str) else actual
    require(digest(actual) == record["template_digest"], "Remote stack template differs; no action permitted")
    resources = aws(record, "cloudformation", "list-stack-resources", "--stack-name", record["stack_id"])["StackResourceSummaries"]
    expected = record['template']['Resources']
    names = [item['LogicalResourceId'] for item in resources]
    require(len(names) == len(set(names)) and set(names) <= set(expected), 'Unexpected/duplicate logical resources')
    if status == 'CREATE_COMPLETE':
        require(set(names) == set(expected), 'Incomplete successful stack inventory')
    require(all(item['ResourceType'] == expected[item['LogicalResourceId']]['Type'] and
                item['ResourceStatus'] in {'CREATE_IN_PROGRESS','CREATE_COMPLETE','CREATE_FAILED','DELETE_COMPLETE','DELETE_FAILED'} for item in resources),
            'Unexpected resource type/import/update/replacement state')
    events = aws(record, 'cloudformation', 'describe-stack-events', '--stack-name', record['stack_id'])['StackEvents']
    require(all(not event['ResourceStatus'].startswith(('IMPORT','UPDATE')) for event in events), 'Stack import or update history forbidden')
    inventory = {}
    for item in resources:
        physical = item.get('PhysicalResourceId')
        if physical:
            unprotected(record, physical)
            logical = item['LogicalResourceId']
            origins = {e.get('PhysicalResourceId') for e in events if e['LogicalResourceId'] == logical and e['ResourceStatus'].startswith('CREATE') and e.get('PhysicalResourceId')}
            require(origins == {physical}, 'Physical identity has no unique creation evidence or was replaced')
            if item['ResourceStatus'] != 'DELETE_COMPLETE':
                inventory[logical] = physical
        else:
            require(status != 'CREATE_COMPLETE' and item['ResourceStatus'] == 'CREATE_FAILED', 'Unknown physical ownership; manual recovery required')
    old = record.get('inventory', {})
    for logical, physical in old.items():
        require(inventory.get(logical) == physical or any(x['LogicalResourceId'] == logical and x.get('PhysicalResourceId') == physical and x['ResourceStatus'] == 'DELETE_COMPLETE' for x in resources),
                'Previously observed resource replaced or disappeared')
    validate_resources(record, inventory)
    record['inventory'] = {**old, **inventory}
    return inventory


def validate_resources(record, resources):
    """Validate live root resources and untaggable relationships; never infer ownership from a name."""
    def one(operation, key, flag, logical):
        value = aws(record, 'ec2', operation, flag, resources[logical])[key]
        require(len(value) == 1, 'Missing/ambiguous staging resource')
        ownership(record, value[0], logical)
        return value[0]
    network = resources.get('Network')
    if network:
        vpc = one('describe-vpcs','Vpcs','--vpc-ids','Network')
        require(vpc['VpcId'] == network and vpc['CidrBlock'] == '10.245.0.0/24' and vpc['OwnerId'] == record['account'], 'VPC identity mismatch')
    for logical, operation, key, flag, id_key in [
            ('Subnet','describe-subnets','Subnets','--subnet-ids','SubnetId'),
            ('Firewall','describe-security-groups','SecurityGroups','--group-ids','GroupId'),
            ('Routes','describe-route-tables','RouteTables','--route-table-ids','RouteTableId')]:
        if logical in resources:
            value = one(operation,key,flag,logical)
            require(network and value['VpcId'] == network and value[id_key] == resources[logical] and value['OwnerId'] == record['account'], 'Network resource crossed isolation boundary')
            if logical == 'Routes':
                if 'DefaultRoute' in resources:
                    require(any(r.get('DestinationCidrBlock') == '0.0.0.0/0' and r.get('GatewayId') == resources.get('Gateway') for r in value['Routes']), 'Default route target changed')
                if 'SubnetRoutes' in resources:
                    require(any(a.get('RouteTableAssociationId') == resources['SubnetRoutes'] and a.get('SubnetId') == resources.get('Subnet') for a in value['Associations']), 'Subnet association changed')
    if 'Gateway' in resources:
        gateway = one('describe-internet-gateways','InternetGateways','--internet-gateway-ids','Gateway')
        require(gateway['InternetGatewayId'] == resources['Gateway'] and gateway['OwnerId'] == record['account'], 'Gateway identity mismatch')
        require(all(a['VpcId'] == network for a in gateway['Attachments']) and
                ('Attachment' not in resources or any(a['VpcId'] == network for a in gateway['Attachments'])), 'Gateway attached outside staging')
    require('DefaultRoute' not in resources or {'Routes','Gateway'} <= resources.keys(), 'Unknown route ownership')
    require('SubnetRoutes' not in resources or {'Routes','Subnet'} <= resources.keys(), 'Unknown association ownership')
    require('Attachment' not in resources or {'Network','Gateway'} <= resources.keys(), 'Unknown gateway attachment ownership')
    if 'Host' in resources:
        reservations = aws(record, 'ec2', 'describe-instances', '--instance-ids', resources['Host'])['Reservations']
        hosts = [h for reservation in reservations for h in reservation['Instances']]
        require(len(hosts) == 1, 'Unknown host ownership')
        host = hosts[0]
        ownership(record, host, 'Host')
        require(host['InstanceId'] == resources['Host'] and host['ImageId'] == record['ami'] and host['VpcId'] == network and host['SubnetId'] == resources.get('Subnet') and
                {g['GroupId'] for g in host['SecurityGroups']} == {resources.get('Firewall')}, 'Host identity/network mismatch')
        unprotected(record, host.get('PublicIpAddress'), host['InstanceId'], host['VpcId'], host['SubnetId'])
        volumes = sorted(x['Ebs']['VolumeId'] for x in host['BlockDeviceMappings'])
        interfaces = sorted(x['NetworkInterfaceId'] for x in host['NetworkInterfaces'])
        for key, ids in [('volumes',volumes),('interfaces',interfaces)]:
            unprotected(record, *ids)
            if record.get('attachments'):
                require(record['attachments'][key] == ids, 'Attached resource replaced; manual investigation required')
        for item in host['BlockDeviceMappings']:
            require(item['Ebs']['DeleteOnTermination'], 'Retained host storage forbidden')
        for volume in volumes:
            data = aws(record,'ec2','describe-volumes','--volume-ids',volume)['Volumes']
            require(len(data) == 1 and data[0]['Encrypted'] and len(data[0]['Attachments']) == 1 and data[0]['Attachments'][0]['InstanceId'] == resources['Host'], 'Volume ownership/attachment mismatch')
            ownership(record, data[0])
        for interface in interfaces:
            data = aws(record,'ec2','describe-network-interfaces','--network-interface-ids',interface)['NetworkInterfaces']
            require(len(data) == 1 and data[0]['OwnerId'] == record['account'] and data[0]['VpcId'] == network and
                    data[0]['SubnetId'] == resources.get('Subnet') and data[0].get('Attachment',{}).get('InstanceId') == resources['Host'], 'Interface ownership mismatch')
        record['attachments'] = {'volumes':volumes, 'interfaces':interfaces}


def inspect(record):
    resources = stack_guard(record)
    require("Host" in resources and "Network" in resources, "Staging instance not ready")
    instances = aws(record, "ec2", "describe-instances", "--instance-ids", resources["Host"])["Reservations"]
    host = instances[0]["Instances"][0]
    tags = {tag["Key"]: tag["Value"] for tag in host["Tags"]}
    ip = host.get("PublicIpAddress", "")
    require(host["VpcId"] == resources["Network"] and host["SubnetId"] == resources["Subnet"] and
            {g["GroupId"] for g in host["SecurityGroups"]} == {resources["Firewall"]} and
            tags.get("StageRun") == record["run_id"] and tags.get("aws:cloudformation:stack-id") == record["stack_id"] and
            host["ImageId"] == record["ami"] and host["State"]["Name"] == "running", "Instance does not match the new staging stack")
    require(ip and ip != PRODUCTION_IP and ipaddress.ip_address(ip).is_global, "Staging cannot target production/private endpoints")
    return {"instance_id": host["InstanceId"], "public_ip": ip, "run_id": record["run_id"],
            "volumes": [item["Ebs"]["VolumeId"] for item in host["BlockDeviceMappings"]],
            "interfaces": [item["NetworkInterfaceId"] for item in host["NetworkInterfaces"]]}


def trust(record):
    target = inspect(record)
    console = aws(record, "ec2", "get-console-output", "--instance-id", target["instance_id"], "--latest").get("Output", "")
    if "RACEBIB_STAGING_TRUST " not in console:
        try:
            console = base64.b64decode(console).decode()
        except (ValueError, UnicodeError):
            pass
    lines = [line.split("RACEBIB_STAGING_TRUST ", 1)[1].strip() for line in console.splitlines() if "RACEBIB_STAGING_TRUST " in line]
    if not lines:
        raise NotReady('Bootstrap trust record is not ready')
    value = json.loads(lines[-1])
    require(all(value[name] == target[name] for name in ("instance_id", "public_ip", "run_id")), "Console trust identity mismatch")
    require(value["domain"] == "stage-" + record["run_id"] + ".racebib.invalid", "Unexpected staging TLS domain")
    target.update(value)
    return target


def observed_stack(record):
    result = aws(record, 'cloudformation', 'describe-stacks', '--stack-name', record.get('stack_id') or record['stack_name'], missing_ok=True)
    if result is None:
        require(record.get('stack_id'), 'Missing creation response is ambiguous; reconcile the unique staging name before teardown')
        return None
    require(len(result.get('Stacks', [])) == 1, 'Expected exactly one staging stack observation')
    stack = result['Stacks'][0]
    ownership(record, stack)
    require(not record.get('stack_id') or stack['StackId'] == record['stack_id'], 'Stack identity changed')
    validate_record(dict(record, stack_id=stack['StackId']))
    return stack


def destroy(directory, record):
    account_guard(record)
    require(record.get('creation_submitted'), 'This tool did not submit the staging stack')
    observed = observed_stack(record)
    if observed is not None and not record.get('stack_id'):
        # Preserve a positively reconciled ARN even if deletion already finished or is in flight.
        record['stack_id'] = observed['StackId']
        save(directory, record)
    if observed is None or observed.get('StackStatus') == 'DELETE_COMPLETE':
        return
    if observed.get('StackStatus') == 'DELETE_IN_PROGRESS':
        aws(record, "cloudformation", "wait", "stack-delete-complete", "--stack-name", observed['StackId'])
        return
    stack_guard(record, deleting=True)
    save(directory, record)
    aws(record, "cloudformation", "delete-stack", "--stack-name", record["stack_id"])
    aws(record, "cloudformation", "wait", "stack-delete-complete", "--stack-name", record["stack_id"])


def explicit_inventory(record, operation, key, flag, id_key, identifier):
    value = aws(record, 'ec2', operation, flag, identifier, missing_ok=True)
    if value is None:  # Only aws()'s operation-specific recognized NotFound path.
        return []
    if (not isinstance(value, dict) or set(value) != {key} or not isinstance(value[key], list) or
            len(value[key]) > 1 or any(not isinstance(item, dict) or item.get(id_key) != identifier for item in value[key])):
        raise InfrastructureFailure('aws-response', 'ec2', operation, exit_code=0)
    return value[key]


def cleanup(record):
    account_guard(record)
    remaining = {}
    filters = ["Name=tag:StageRun,Values=" + record["run_id"], "Name=tag:Environment,Values=racebib-staging"]
    for operation, key in [("describe-instances", "Reservations"), ("describe-volumes", "Volumes"),
                           ("describe-addresses", "Addresses"), ("describe-nat-gateways", "NatGateways"), ("describe-snapshots", "Snapshots"),
                           ("describe-network-interfaces", "NetworkInterfaces"), ("describe-vpcs", "Vpcs")]:
        args = [operation, "--filters", *filters]
        if operation == "describe-snapshots":
            args += ["--owner-ids", "self"]
        response = aws(record, "ec2", *args)
        if not isinstance(response, dict) or not isinstance(response.get(key), list):
            raise InfrastructureFailure("aws-response", "ec2", operation, exit_code=0)
        value = response[key]
        if operation == "describe-instances":
            value = [host["InstanceId"] for reservation in value for host in reservation["Instances"] if host["State"]["Name"] != "terminated"]
        if value:
            remaining[operation] = value
    target = record.get('target') if record.get('target') is not None else {}
    attachments = record.get('attachments') if record.get('attachments') is not None else {}
    require(isinstance(target, dict) and isinstance(attachments, dict), 'Invalid partial staging inventory')
    for inventory in (target, attachments):
        require(all(isinstance(inventory.get(key, []), list) and
                    all(isinstance(item, str) and item for item in inventory.get(key, []))
                    for key in ('interfaces', 'volumes')), 'Invalid tracked attachment identifiers')
    for interface in sorted(set(target.get("interfaces", []) + attachments.get("interfaces", []))):
        unprotected(record, interface)
        value = explicit_inventory(record, 'describe-network-interfaces', 'NetworkInterfaces', '--network-interface-ids', 'NetworkInterfaceId', interface)
        if value:
            remaining[interface] = "still present"
    for volume in sorted(set(target.get('volumes', []) + attachments.get('volumes', []))):
        unprotected(record, volume)
        value = explicit_inventory(record, 'describe-volumes', 'Volumes', '--volume-ids', 'VolumeId', volume)
        if value:
            remaining[volume] = 'tracked volume still present, even if tags were removed'
    require(not remaining, "Staging resources remain; inspect the cleanup report, never delete unrelated resources")
    stack = observed_stack(record)
    require(stack is None or stack["StackStatus"] == "DELETE_COMPLETE", "Staging stack still exists")
    return {"run_id": record["run_id"], "tracked_and_tagged_billable_resources": "none", "account_wide_billing_certification": False}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("action", choices=["plan", "create", "inspect", "trust", "destroy", "cleanup"])
    parser.add_argument("--directory", required=True)
    parser.add_argument("--input")
    parser.add_argument("--approved-run")
    parser.add_argument("--execution-id")
    parser.add_argument("--aws-cli")
    args = parser.parse_args()
    directory = Path(args.directory)
    if args.action == "plan":
        config = json.loads(Path(args.input).read_text(encoding="utf-8-sig"))
        value = plan(directory, **config)
        print(json.dumps({name: value[name] for name in ("run_id", "estimate_usd", "stack_name")}))
        return
    record = json.loads((directory / "run.json").read_text(encoding="utf-8-sig"))
    require(args.aws_cli and Path(args.aws_cli).is_absolute(), 'Reviewed absolute AWS CLI path required')
    record['aws_cli'] = args.aws_cli
    validate_record(record)
    if args.action == "create":
        value = create(directory, record, args.approved_run, args.execution_id)
        record["target"] = value
    elif args.action in ("inspect", "trust", "cleanup"):
        value = globals()[args.action](record)
        if args.action != "cleanup":
            record["target"] = value
    else:
        destroy(directory, record)
        value = {"stack_deleted": True}
    if args.action != "cleanup":
        save(directory, record)
    print(json.dumps(value))


def cli():
    try:
        main()
    except Exception as error:
        # The shared production native runner discards stderr. Publish ONLY this safe envelope on stdout.
        print(json.dumps(diagnostic(error, sys.argv[1] if len(sys.argv) > 1 else "unknown")))
        print("Staging operation failed; sanitized diagnostic on stdout; raw details withheld.", file=sys.stderr)
        return 2 if isinstance(error, NotReady) else 1
    return 0


if __name__ == "__main__":
    sys.exit(cli())
