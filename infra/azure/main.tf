provider "azurerm" {
  features {
    key_vault {
      purge_soft_delete_on_destroy    = true
      recover_soft_deleted_key_vaults = false
    }
  }
}

locals {
  name = "${var.project_name}-${var.environment}"
  # ACR names must be globally unique, alphanumeric, 5-50 chars
  acr_name = "${replace(var.project_name, "-", "")}${var.environment}acr"
}

data "azurerm_client_config" "current" {}

# ─────────────────────────────────────────────
# Resource group
# ─────────────────────────────────────────────

resource "azurerm_resource_group" "main" {
  name     = var.resource_group_name
  location = var.location
}

# ─────────────────────────────────────────────
# Container Registry (ACR)
# ─────────────────────────────────────────────

resource "azurerm_container_registry" "main" {
  name                = local.acr_name
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "Basic"
  admin_enabled       = true
}

# ─────────────────────────────────────────────
# PostgreSQL Flexible Server
# ─────────────────────────────────────────────

resource "azurerm_postgresql_flexible_server" "main" {
  name                   = "${local.name}-psql"
  resource_group_name    = azurerm_resource_group.main.name
  location               = azurerm_resource_group.main.location
  version                = "16"
  administrator_login    = "fabricate"
  administrator_password = var.db_admin_password
  storage_mb             = 32768
  sku_name               = "B_Standard_B1ms"

  # Allow public access so the Container App can reach it without VNet integration on Basic tier
  public_network_access_enabled = true
}

resource "azurerm_postgresql_flexible_server_database" "app" {
  name      = "fabricate"
  server_id = azurerm_postgresql_flexible_server.main.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "allow_azure" {
  name             = "allow-azure-services"
  server_id        = azurerm_postgresql_flexible_server.main.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# ─────────────────────────────────────────────
# Key Vault
# ─────────────────────────────────────────────

resource "azurerm_key_vault" "main" {
  name                        = "${local.name}-kv"
  location                    = azurerm_resource_group.main.location
  resource_group_name         = azurerm_resource_group.main.name
  tenant_id                   = data.azurerm_client_config.current.tenant_id
  sku_name                    = "standard"
  purge_protection_enabled    = false
  soft_delete_retention_days  = 7

  access_policy {
    tenant_id = data.azurerm_client_config.current.tenant_id
    object_id = data.azurerm_client_config.current.object_id

    secret_permissions = ["Get", "List", "Set", "Delete", "Purge"]
  }
}

resource "azurerm_key_vault_secret" "bootstrap_key" {
  name         = "bootstrap-api-key"
  value        = var.bootstrap_api_key
  key_vault_id = azurerm_key_vault.main.id
}

# ─────────────────────────────────────────────
# Managed Identity for Container App
# ─────────────────────────────────────────────

resource "azurerm_user_assigned_identity" "app" {
  name                = "${local.name}-identity"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
}

resource "azurerm_key_vault_access_policy" "app" {
  key_vault_id = azurerm_key_vault.main.id
  tenant_id    = data.azurerm_client_config.current.tenant_id
  object_id    = azurerm_user_assigned_identity.app.principal_id

  secret_permissions = ["Get", "List"]
}

resource "azurerm_role_assignment" "acr_pull" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.app.principal_id
}

# ─────────────────────────────────────────────
# Log Analytics + Container App Environment
# ─────────────────────────────────────────────

resource "azurerm_log_analytics_workspace" "main" {
  name                = "${local.name}-logs"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "PerGB2018"
  retention_in_days   = 7
}

resource "azurerm_container_app_environment" "main" {
  name                       = "${local.name}-cae"
  location                   = azurerm_resource_group.main.location
  resource_group_name        = azurerm_resource_group.main.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id
}

# ─────────────────────────────────────────────
# Container App
# ─────────────────────────────────────────────

resource "azurerm_container_app" "api" {
  name                         = "${local.name}-api"
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app.id]
  }

  registry {
    server   = azurerm_container_registry.main.login_server
    identity = azurerm_user_assigned_identity.app.id
  }

  template {
    container {
      name   = "api"
      image  = "${azurerm_container_registry.main.login_server}/${var.project_name}:${var.image_tag}"
      cpu    = var.container_cpu
      memory = var.container_memory

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }

      env {
        name  = "SchemaProvider__Provider"
        value = "PostgreSQL"
      }

      env {
        name  = "ConnectionStrings__DefaultConnection"
        value = "Host=${azurerm_postgresql_flexible_server.main.fqdn};Database=fabricate;Username=fabricate;Password=${var.db_admin_password};SslMode=Require"
      }

      # Application persistence (accounts, API keys, sessions, credentials). Without these the API runs in-memory
      # and loses state on every revision; migrations are applied at startup.
      env {
        name  = "FABRICATE_DB_PROVIDER"
        value = "postgres"
      }

      env {
        name  = "FABRICATE_CONNECTION_STRING"
        value = "Host=${azurerm_postgresql_flexible_server.main.fqdn};Database=fabricate;Username=fabricate;Password=${var.db_admin_password};SSL Mode=Require"
      }

      env {
        name  = "FABRICATE_DATA_PROTECTION_KEYS_PATH"
        value = "/app/keys"
      }

      env {
        name  = "FABRICATE__BootstrapApiKey"
        value = var.bootstrap_api_key
      }

      liveness_probe {
        transport = "HTTP"
        path      = "/healthz"
        port      = 8080
      }

      readiness_probe {
        transport = "HTTP"
        path      = "/healthz"
        port      = 8080
      }
    }

    min_replicas = 1
    max_replicas = 3
  }

  ingress {
    external_enabled = true
    target_port      = 8080

    traffic_weight {
      percentage      = 100
      latest_revision = true
    }
  }
}

# ─────────────────────────────────────────────
# Post-deployment smoke tests
# ─────────────────────────────────────────────

resource "null_resource" "smoke_tests" {
  depends_on = [azurerm_container_app.api]

  triggers = {
    app_revision = azurerm_container_app.api.latest_revision_name
  }

  provisioner "local-exec" {
    command = <<-EOT
      echo "Waiting 30s for Container App to stabilise..."
      sleep 30
      dotnet test ${path.module}/../../Fabricate.Tests.Smoke \
        --logger "console;verbosity=normal" \
        -e SMOKE_API_BASE_URL=https://${azurerm_container_app.api.ingress[0].fqdn} \
        -e SMOKE_API_KEY=${var.bootstrap_api_key}
    EOT
  }
}
