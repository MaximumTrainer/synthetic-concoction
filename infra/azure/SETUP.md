# Azure Deployment Setup Guide

Step-by-step guide to deploying Fabricate on **Azure** using Container Apps, PostgreSQL Flexible Server, ACR, and Key Vault in `uksouth`.

---

## 1. Prerequisites

| Tool | Install |
|---|---|
| [Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli) | `brew install azure-cli` or [installer](https://aka.ms/installazurecliwindows) |
| [Terraform ≥ 1.6](https://developer.hashicorp.com/terraform/downloads) | `brew tap hashicorp/tap && brew install hashicorp/tap/terraform` |
| [Docker](https://docs.docker.com/get-docker/) | Docker Desktop or `brew install docker` |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Required to run smoke tests post-deploy |

---

## 2. Azure account setup

### 2.1 Sign in

```bash
az login
```

If you have multiple subscriptions, set the active one:

```bash
az account list --output table
az account set --subscription "<subscription-name-or-id>"
```

Confirm:

```bash
az account show --output table
```

### 2.2 Create a service principal for Terraform

```bash
az ad sp create-for-rbac \
  --name "fabricate-terraform" \
  --role "Contributor" \
  --scopes /subscriptions/<subscription-id> \
  --sdk-auth
```

Save the JSON output — it contains `clientId`, `clientSecret`, `subscriptionId`, and `tenantId`. This is your `AZURE_CREDENTIALS` secret for GitHub Actions.

### 2.3 Grant Key Vault permissions to the service principal

The Terraform provider needs to manage Key Vault access policies. The Contributor role is sufficient for most resources, but add an explicit role for Key Vault secrets management:

```bash
az role assignment create \
  --assignee <clientId-from-above> \
  --role "Key Vault Administrator" \
  --scope /subscriptions/<subscription-id>
```

---

## 3. Configure Terraform variables

```bash
cd infra/azure
cp terraform.tfvars.example terraform.tfvars
```

Edit `terraform.tfvars` — **never commit this file**:

```hcl
location            = "uksouth"
resource_group_name = "rg-fabricate"
project_name        = "fabricate"
environment         = "prod"

# Set after first apply (see section 5):
image_tag = "latest"

# Secure values:
db_admin_password = "a-strong-password-here"
bootstrap_api_key = "cnc_your-secret-bootstrap-key"
```

Set Terraform Azure provider authentication via environment variables:

```bash
export ARM_CLIENT_ID="<clientId>"
export ARM_CLIENT_SECRET="<clientSecret>"
export ARM_SUBSCRIPTION_ID="<subscriptionId>"
export ARM_TENANT_ID="<tenantId>"
```

Or add them to your shell profile.

---

## 4. First apply — create registry

Run an initial apply to provision the ACR (you need the login server before pushing the image):

```bash
cd infra/azure
terraform init
terraform apply -target=azurerm_container_registry.main
```

Get the registry details:

```bash
terraform output acr_login_server
# → fabricateprodacr.azurecr.io
```

---

## 5. Build and push the Docker image

From the **repository root**:

```bash
# Build
docker build -t fabricate:latest .

# Authenticate Docker with ACR
ACR=$(cd infra/azure && terraform output -raw acr_login_server)
az acr login --name ${ACR%%.*}

# Tag and push
docker tag fabricate:latest ${ACR}/fabricate:latest
docker push ${ACR}/fabricate:latest
```

The `image_tag` variable defaults to `latest` so no update is required for a first deploy.

---

## 6. Full deployment

```bash
cd infra/azure
terraform apply
```

Terraform will:
1. Create the Resource Group
2. Create ACR (if not already created)
3. Create PostgreSQL Flexible Server
4. Create Key Vault and store the bootstrap API key
5. Create a managed identity and assign ACR Pull + Key Vault access
6. Create Log Analytics workspace and Container App Environment
7. Create the Container App with external ingress on port 8080
8. Wait 30 seconds then run smoke tests

Expected output after apply:

```
api_url            = "https://fabricate-prod-api.uksouth.azurecontainerapps.io"
acr_login_server   = "fabricateprodacr.azurecr.io"
postgresql_fqdn    = "fabricate-prod-psql.postgres.database.azure.com"
```

---

## 7. Verify the deployment

```bash
API_URL=$(cd infra/azure && terraform output -raw api_url)

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

Add the following **repository secrets**:

| Secret | Value |
|---|---|
| `AZURE_CREDENTIALS` | Full JSON output from `az ad sp create-for-rbac --sdk-auth` |
| `ARM_CLIENT_ID` | Service principal `clientId` |
| `ARM_CLIENT_SECRET` | Service principal `clientSecret` |
| `ARM_SUBSCRIPTION_ID` | Azure subscription ID |
| `ARM_TENANT_ID` | Azure tenant ID |
| `ACR_USERNAME` | ACR admin username (from `terraform output acr_admin_username`) |
| `ACR_PASSWORD` | ACR admin password (from Azure Portal or `az acr credential show`) |
| `AZURE_DB_PASSWORD` | PostgreSQL admin password |
| `BOOTSTRAP_API_KEY` | Bootstrap API key plaintext |

Add **repository variables**:

| Variable | Value |
|---|---|
| `ACR_LOGIN_SERVER` | e.g. `fabricateprodacr.azurecr.io` |
| `AZURE_LOCATION` | `uksouth` |

Trigger a deployment:

```
GitHub → Actions → Deploy → Run workflow → cloud: azure
```

---

## 9. Useful commands

```bash
# View Container App logs (stream)
az containerapp logs show \
  --name fabricate-prod-api \
  --resource-group rg-fabricate \
  --follow

# List Container App revisions
az containerapp revision list \
  --name fabricate-prod-api \
  --resource-group rg-fabricate \
  --output table

# Force a new revision after pushing an image
az containerapp update \
  --name fabricate-prod-api \
  --resource-group rg-fabricate \
  --image fabricateprodacr.azurecr.io/fabricate:latest

# Show PostgreSQL connection details
az postgres flexible-server show \
  --name fabricate-prod-psql \
  --resource-group rg-fabricate \
  --query "{fqdn:fullyQualifiedDomainName, adminLogin:administratorLogin}"
```

---

## 10. Teardown

```bash
cd infra/azure
terraform destroy
```

Or destroy the entire resource group (faster):

```bash
az group delete --name rg-fabricate --yes --no-wait
```

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Container App fails to start | Image pull error | Check managed identity has `AcrPull` role on ACR |
| 401 on all API calls | Bootstrap key not injected | Verify Key Vault secret and Container App env var `FABRICATE__BootstrapApiKey` |
| PostgreSQL connection refused | Firewall rule | Terraform adds `allow-azure-services` rule; check it's present |
| Terraform Key Vault error | Soft-delete conflict | Purge previously deleted vault: `az keyvault purge --name <vault>` |
| `AuthorizationFailed` during apply | Missing role | Ensure SP has Contributor + Key Vault Administrator on subscription |
