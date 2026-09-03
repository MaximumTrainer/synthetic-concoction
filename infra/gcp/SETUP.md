# GCP Deployment Setup Guide

Step-by-step guide to deploying Fabricate on **GCP** using Cloud Run, Cloud SQL PostgreSQL, Artifact Registry, and Secret Manager in `europe-west2`.

---

## 1. Prerequisites

| Tool | Install |
|---|---|
| [gcloud CLI](https://cloud.google.com/sdk/docs/install) | `brew install google-cloud-sdk` or [installer](https://cloud.google.com/sdk/docs/install) |
| [Terraform ≥ 1.6](https://developer.hashicorp.com/terraform/downloads) | `brew tap hashicorp/tap && brew install hashicorp/tap/terraform` |
| [Docker](https://docs.docker.com/get-docker/) | Docker Desktop or `brew install docker` |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Required to run smoke tests post-deploy |

---

## 2. GCP project setup

### 2.1 Create or select a project

```bash
# List existing projects
gcloud projects list

# Create a new project (optional)
gcloud projects create fabricate-prod --name="Fabricate"

# Set the active project
gcloud config set project fabricate-prod
```

### 2.2 Enable billing

Cloud Run, Cloud SQL, and Artifact Registry require billing to be enabled.

1. Open [console.cloud.google.com/billing](https://console.cloud.google.com/billing)
2. Link a billing account to your project

### 2.3 Authenticate

```bash
# Authenticate with your user account
gcloud auth login

# Set application-default credentials (used by Terraform)
gcloud auth application-default login
```

### 2.4 Create a service account for Terraform

For production or CI/CD use, create a dedicated service account instead of using your personal credentials:

```bash
# Create service account
gcloud iam service-accounts create fabricate-terraform \
  --display-name="Fabricate Terraform"

SA_EMAIL="fabricate-terraform@$(gcloud config get-value project).iam.gserviceaccount.com"

# Grant required roles
for ROLE in \
  roles/run.admin \
  roles/cloudsql.admin \
  roles/secretmanager.admin \
  roles/artifactregistry.admin \
  roles/iam.serviceAccountAdmin \
  roles/iam.serviceAccountUser \
  roles/serviceusage.serviceUsageAdmin; do
  gcloud projects add-iam-policy-binding $(gcloud config get-value project) \
    --member="serviceAccount:${SA_EMAIL}" \
    --role="${ROLE}"
done

# Create and download a key
gcloud iam service-accounts keys create ~/fabricate-terraform-key.json \
  --iam-account="${SA_EMAIL}"

# Activate the service account (for Terraform)
export GOOGLE_APPLICATION_CREDENTIALS=~/fabricate-terraform-key.json
```

---

## 3. Configure Terraform variables

```bash
cd infra/gcp
cp terraform.tfvars.example terraform.tfvars
```

Edit `terraform.tfvars` — **never commit this file**:

```hcl
project_id   = "fabricate-prod"   # Your GCP project ID
region       = "europe-west2"
project_name = "fabricate"
environment  = "prod"

# Set after first apply (see section 5):
image_tag = "latest"

# Secure values:
db_password       = "a-strong-password-here"
bootstrap_api_key = "cnc_your-secret-bootstrap-key"
```

---

## 4. First apply — enable APIs and create registry

Enable APIs and create the Artifact Registry repository (you need it before pushing the image):

```bash
cd infra/gcp
terraform init
terraform apply \
  -target=google_project_service.apis \
  -target=google_artifact_registry_repository.app
```

> The first `terraform apply` may take 2–3 minutes while GCP enables APIs.

Get the registry URL:

```bash
terraform output artifact_registry_url
# → europe-west2-docker.pkg.dev/fabricate-prod/fabricate
```

---

## 5. Build and push the Docker image

From the **repository root**:

```bash
# Build
docker build -t fabricate:latest .

# Configure Docker to use Artifact Registry
gcloud auth configure-docker europe-west2-docker.pkg.dev

# Tag and push
REGISTRY=$(cd infra/gcp && terraform output -raw artifact_registry_url)
docker tag fabricate:latest ${REGISTRY}/fabricate:latest
docker push ${REGISTRY}/fabricate:latest
```

---

## 6. Full deployment

```bash
cd infra/gcp
terraform apply
```

Terraform will:
1. Enable required GCP APIs (Cloud Run, Cloud SQL, Secret Manager, Artifact Registry)
2. Create Artifact Registry repository
3. Create Cloud SQL PostgreSQL 16 instance
4. Create Secret Manager secret with the bootstrap API key
5. Create a service account with appropriate IAM bindings
6. Create the Cloud Run service (publicly accessible, API-key auth at application layer)
7. Wait 30 seconds then run smoke tests

Expected output after apply:

```
api_url               = "https://fabricate-prod-abc123-ew.a.run.app"
artifact_registry_url = "europe-west2-docker.pkg.dev/fabricate-prod/fabricate"
cloud_sql_ip          = "34.89.xxx.xxx"
service_account_email = "fabricate-prod-sa@fabricate-prod.iam.gserviceaccount.com"
```

---

## 7. Verify the deployment

```bash
API_URL=$(cd infra/gcp && terraform output -raw api_url)

# Health check (no auth)
curl ${API_URL}/healthz

# API call
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

Create a service account key for CI (or use [Workload Identity Federation](https://cloud.google.com/iam/docs/workload-identity-federation) for keyless auth):

```bash
gcloud iam service-accounts keys create /tmp/gcp-ci-key.json \
  --iam-account="${SA_EMAIL}"

cat /tmp/gcp-ci-key.json   # copy this value
```

Add the following **repository secrets**:

| Secret | Value |
|---|---|
| `GCP_CREDENTIALS_JSON` | Full content of the service account key JSON |
| `GCP_DB_PASSWORD` | Cloud SQL password |
| `BOOTSTRAP_API_KEY` | Bootstrap API key plaintext |

Add **repository variables**:

| Variable | Value |
|---|---|
| `GCP_PROJECT_ID` | e.g. `fabricate-prod` |
| `GCP_REGION` | `europe-west2` |

Trigger a deployment:

```
GitHub → Actions → Deploy → Run workflow → cloud: gcp
```

---

## 9. Useful commands

```bash
# Stream Cloud Run logs
gcloud run services logs tail fabricate-prod \
  --region europe-west2 \
  --project fabricate-prod

# Describe the Cloud Run service
gcloud run services describe fabricate-prod \
  --region europe-west2 \
  --format="yaml(status)"

# Force new Cloud Run revision (after pushing a new image)
gcloud run services update fabricate-prod \
  --region europe-west2 \
  --image europe-west2-docker.pkg.dev/fabricate-prod/fabricate/fabricate:latest

# List Cloud SQL instances
gcloud sql instances list

# Connect to Cloud SQL (via Cloud SQL Auth Proxy)
gcloud sql connect fabricate-prod-psql --user=fabricate

# View a secret version
gcloud secrets versions access latest \
  --secret fabricate-prod-bootstrap-api-key
```

---

## 10. Teardown

```bash
cd infra/gcp
terraform destroy
```

> Cloud SQL instances are set to `deletion_protection = false` for easy teardown. Enable it in production.

To delete the entire project (removes all resources and billing):

```bash
gcloud projects delete fabricate-prod
```

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `PERMISSION_DENIED` during apply | Missing IAM role | Add missing role to service account (see section 2.4) |
| Cloud Run returns 403 | Missing `allUsers` invoker binding | Terraform applies it; check `google_cloud_run_v2_service_iam_member.public` |
| Container fails to pull image | SA lacks registry access | Verify `roles/artifactregistry.reader` on SA |
| `FABRICATE__BootstrapApiKey` not set | Secret version not accessible | Check SA has `roles/secretmanager.secretAccessor` |
| Cloud SQL connection refused | Wrong IP / firewall | `authorized_networks` in Terraform allows `0.0.0.0/0` for simplicity; check it's applied |
| `API not enabled` error | APIs not yet enabled | Re-run `terraform apply -target=google_project_service.apis` |
