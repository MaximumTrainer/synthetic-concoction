provider "google" {
  project = var.project_id
  region  = var.region
}

locals {
  name            = "${var.project_name}-${var.environment}"
  artifact_region = var.region
}

# ─────────────────────────────────────────────
# Enable required APIs
# ─────────────────────────────────────────────

resource "google_project_service" "apis" {
  for_each = toset([
    "run.googleapis.com",
    "sqladmin.googleapis.com",
    "secretmanager.googleapis.com",
    "artifactregistry.googleapis.com",
    "iam.googleapis.com",
  ])
  service            = each.value
  disable_on_destroy = false
}

# ─────────────────────────────────────────────
# Artifact Registry
# ─────────────────────────────────────────────

resource "google_artifact_registry_repository" "app" {
  repository_id = var.project_name
  location      = local.artifact_region
  format        = "DOCKER"
  description   = "Fabricate API container images"

  depends_on = [google_project_service.apis]
}

# ─────────────────────────────────────────────
# Cloud SQL PostgreSQL
# ─────────────────────────────────────────────

resource "google_sql_database_instance" "main" {
  name             = "${local.name}-psql"
  database_version = "POSTGRES_16"
  region           = var.region

  settings {
    tier = "db-f1-micro"

    ip_configuration {
      ipv4_enabled = true
      authorized_networks {
        name  = "all"
        value = "0.0.0.0/0"
      }
    }

    backup_configuration {
      enabled = false
    }
  }

  deletion_protection = false
  depends_on          = [google_project_service.apis]
}

resource "google_sql_database" "app" {
  name     = "fabricate"
  instance = google_sql_database_instance.main.name
}

resource "google_sql_user" "app" {
  name     = "fabricate"
  instance = google_sql_database_instance.main.name
  password = var.db_password
}

# ─────────────────────────────────────────────
# Secret Manager
# ─────────────────────────────────────────────

resource "google_secret_manager_secret" "bootstrap_key" {
  secret_id = "${local.name}-bootstrap-api-key"

  replication {
    auto {}
  }

  depends_on = [google_project_service.apis]
}

resource "google_secret_manager_secret_version" "bootstrap_key" {
  secret      = google_secret_manager_secret.bootstrap_key.id
  secret_data = var.bootstrap_api_key
}

# ─────────────────────────────────────────────
# Service account for Cloud Run
# ─────────────────────────────────────────────

resource "google_service_account" "app" {
  account_id   = "${local.name}-sa"
  display_name = "Fabricate API service account"
}

resource "google_secret_manager_secret_iam_member" "app_bootstrap" {
  secret_id = google_secret_manager_secret.bootstrap_key.secret_id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.app.email}"
}

resource "google_artifact_registry_repository_iam_member" "app_pull" {
  location   = google_artifact_registry_repository.app.location
  repository = google_artifact_registry_repository.app.name
  role       = "roles/artifactregistry.reader"
  member     = "serviceAccount:${google_service_account.app.email}"
}

# ─────────────────────────────────────────────
# Cloud Run service
# ─────────────────────────────────────────────

resource "google_cloud_run_v2_service" "api" {
  name     = local.name
  location = var.region

  template {
    service_account = google_service_account.app.email

    scaling {
      min_instance_count = var.cloud_run_min_instances
      max_instance_count = var.cloud_run_max_instances
    }

    containers {
      image = "${local.artifact_region}-docker.pkg.dev/${var.project_id}/${google_artifact_registry_repository.app.repository_id}/${var.project_name}:${var.image_tag}"

      ports {
        container_port = 8080
      }

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
        value = "Host=${google_sql_database_instance.main.public_ip_address};Database=fabricate;Username=fabricate;Password=${var.db_password}"
      }

      # Application persistence (accounts, API keys, sessions, credentials). Without these the API runs in-memory
      # and loses state on every revision; migrations are applied at startup.
      env {
        name  = "FABRICATE_DB_PROVIDER"
        value = "postgres"
      }

      env {
        name  = "FABRICATE_CONNECTION_STRING"
        value = "Host=${google_sql_database_instance.main.public_ip_address};Database=fabricate;Username=fabricate;Password=${var.db_password};SSL Mode=Require;Trust Server Certificate=true"
      }

      env {
        name  = "FABRICATE_DATA_PROTECTION_KEYS_PATH"
        value = "/app/keys"
      }

      env {
        name = "FABRICATE__BootstrapApiKey"
        value_source {
          secret_key_ref {
            secret  = google_secret_manager_secret.bootstrap_key.secret_id
            version = "latest"
          }
        }
      }

      startup_probe {
        http_get {
          path = "/healthz"
          port = 8080
        }
        initial_delay_seconds = 10
        period_seconds        = 5
        failure_threshold     = 6
      }

      liveness_probe {
        http_get {
          path = "/healthz"
          port = 8080
        }
        period_seconds    = 30
        failure_threshold = 3
      }
    }
  }

  depends_on = [
    google_project_service.apis,
    google_sql_database_instance.main,
  ]
}

# Allow unauthenticated requests (API-key auth is handled at the app level)
resource "google_cloud_run_v2_service_iam_member" "public" {
  project  = var.project_id
  location = google_cloud_run_v2_service.api.location
  name     = google_cloud_run_v2_service.api.name
  role     = "roles/run.invoker"
  member   = "allUsers"
}

# ─────────────────────────────────────────────
# Post-deployment smoke tests
# ─────────────────────────────────────────────

resource "null_resource" "smoke_tests" {
  depends_on = [
    google_cloud_run_v2_service.api,
    google_cloud_run_v2_service_iam_member.public,
  ]

  triggers = {
    service_uri = google_cloud_run_v2_service.api.uri
  }

  provisioner "local-exec" {
    command = <<-EOT
      echo "Waiting 30s for Cloud Run service to stabilise..."
      sleep 30
      dotnet test ${path.module}/../../Fabricate.Tests.Smoke \
        --logger "console;verbosity=normal" \
        -e SMOKE_API_BASE_URL=${google_cloud_run_v2_service.api.uri} \
        -e SMOKE_API_KEY=${var.bootstrap_api_key}
    EOT
  }
}
