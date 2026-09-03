# AWS Deployment Setup Guide

Step-by-step guide to deploying Fabricate on **AWS** using ECS Fargate, ALB, RDS PostgreSQL, ECR, and Secrets Manager in `eu-west-2`.

---

## 1. Prerequisites

| Tool | Install |
|---|---|
| [AWS CLI v2](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html) | `brew install awscli` or [installer](https://awscli.amazonaws.com/AWSCLIV2.pkg) |
| [Terraform ≥ 1.6](https://developer.hashicorp.com/terraform/downloads) | `brew tap hashicorp/tap && brew install hashicorp/tap/terraform` |
| [Docker](https://docs.docker.com/get-docker/) | Docker Desktop or `brew install docker` |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Required to run smoke tests post-deploy |

---

## 2. AWS account setup

### 2.1 Create an IAM user for Terraform

> Skip this step if you already have credentials with sufficient permissions.

1. Sign in to the [AWS Console](https://console.aws.amazon.com/iam)
2. **IAM → Users → Create user**
   - Username: `fabricate-deploy`
   - Access type: **Programmatic access**
3. Attach the following managed policies:
   - `AmazonEC2FullAccess`
   - `AmazonECS_FullAccess`
   - `AmazonRDSFullAccess`
   - `AmazonEC2ContainerRegistryFullAccess`
   - `IAMFullAccess`
   - `SecretsManagerReadWrite`
   - `CloudWatchFullAccess`
   - `ElasticLoadBalancingFullAccess`
4. Download the **Access key ID** and **Secret access key**

### 2.2 Configure the AWS CLI

```bash
aws configure
# AWS Access Key ID:     <your access key>
# AWS Secret Access Key: <your secret key>
# Default region name:   eu-west-2
# Default output format: json
```

Verify:

```bash
aws sts get-caller-identity
```

---

## 3. Configure Terraform variables

```bash
cd infra/aws
cp terraform.tfvars.example terraform.tfvars
```

Edit `terraform.tfvars` — **never commit this file**:

```hcl
aws_region        = "eu-west-2"
project_name      = "fabricate"
environment       = "prod"

# Set after first apply (see section 5):
image_uri = "PLACEHOLDER"

# Secure values — use a password manager to generate these:
db_password       = "a-strong-password-here"
bootstrap_api_key = "cnc_your-secret-bootstrap-key"
```

`terraform.tfvars` is listed in `.gitignore` — confirm it won't be committed:

```bash
git check-ignore -v terraform.tfvars
```

---

## 4. First apply — create registry

Run an initial apply to provision the ECR repository (you need the registry URL before pushing the image):

```bash
cd infra/aws
terraform init
terraform apply -target=aws_ecr_repository.app
```

Get the repository URL:

```bash
terraform output ecr_repository_url
# → 123456789012.dkr.ecr.eu-west-2.amazonaws.com/fabricate-prod
```

---

## 5. Build and push the Docker image

From the **repository root**:

```bash
# Build
docker build -t fabricate:latest .

# Authenticate Docker with ECR
aws ecr get-login-password --region eu-west-2 \
  | docker login --username AWS --password-stdin \
    $(terraform -chdir=infra/aws output -raw ecr_repository_url | cut -d/ -f1)

# Tag
ECR_URI=$(cd infra/aws && terraform output -raw ecr_repository_url)
docker tag fabricate:latest ${ECR_URI}:latest

# Push
docker push ${ECR_URI}:latest
```

Update `image_uri` in `infra/aws/terraform.tfvars`:

```hcl
image_uri = "123456789012.dkr.ecr.eu-west-2.amazonaws.com/fabricate-prod:latest"
```

---

## 6. Full deployment

```bash
cd infra/aws
terraform apply
```

Terraform will:
1. Create VPC, subnets, security groups
2. Create RDS PostgreSQL instance (in private subnets)
3. Store the bootstrap API key in Secrets Manager
4. Create ECS cluster, task definition, and Fargate service
5. Create ALB with health-check on `/healthz`
6. Wait 60 seconds then run smoke tests

Expected output after apply:

```
api_url                = "http://fabricate-prod-alb-1234567890.eu-west-2.elb.amazonaws.com"
ecr_repository_url     = "123456789012.dkr.ecr.eu-west-2.amazonaws.com/fabricate-prod"
ecs_cluster_name       = "fabricate-prod"
rds_endpoint           = "fabricate-prod-postgres.abc123.eu-west-2.rds.amazonaws.com"
```

---

## 7. Verify the deployment

```bash
API_URL=$(cd infra/aws && terraform output -raw api_url)

# Health check (no auth)
curl ${API_URL}/healthz

# API call (replace with your bootstrap key)
curl -H "X-Api-Key: cnc_your-secret-bootstrap-key" \
  ${API_URL}/accounts/00000000-0000-0000-0000-000000000001
```

Run smoke tests manually:

```bash
dotnet test Fabricate.Tests.Smoke \
  -e SMOKE_API_BASE_URL=${API_URL} \
  -e SMOKE_API_KEY=cnc_your-secret-bootstrap-key
```

---

## 8. CI/CD setup (GitHub Actions)

Add the following **repository secrets** in GitHub → Settings → Secrets and variables → Actions:

| Secret | Value |
|---|---|
| `AWS_ACCESS_KEY_ID` | IAM user access key |
| `AWS_SECRET_ACCESS_KEY` | IAM user secret key |
| `AWS_DB_PASSWORD` | RDS password (same as `db_password` in tfvars) |
| `BOOTSTRAP_API_KEY` | Bootstrap API key plaintext |

Add **repository variables**:

| Variable | Value |
|---|---|
| `AWS_REGION` | `eu-west-2` |

Trigger a deployment:

```
GitHub → Actions → Deploy → Run workflow → cloud: aws
```

---

## 9. Useful commands

```bash
# View ECS service status
aws ecs describe-services \
  --cluster fabricate-prod \
  --services fabricate-prod \
  --region eu-west-2

# Tail ECS logs (replace <task-id>)
aws logs tail /ecs/fabricate-prod --follow

# Force new ECS deployment (after pushing a new image)
aws ecs update-service \
  --cluster fabricate-prod \
  --service fabricate-prod \
  --force-new-deployment \
  --region eu-west-2
```

---

## 10. Teardown

```bash
cd infra/aws
terraform destroy
```

> RDS has `deletion_protection = false` and `skip_final_snapshot = true` for easy teardown. Change these for production.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| ECS task keeps stopping | Container unhealthy | Check CloudWatch logs: `aws logs tail /ecs/fabricate-prod` |
| ALB returns 502 | Task not yet healthy | Wait 60s, check target group health |
| `UnauthorizedException` pulling image | ECR auth expired | Re-run `aws ecr get-login-password` + `docker login` |
| Smoke tests fail with 401 | Bootstrap key mismatch | Ensure `SMOKE_API_KEY` matches `bootstrap_api_key` in tfvars |
| RDS connection refused | Security group | Ensure ECS task SG is in `db` SG's ingress rule |
