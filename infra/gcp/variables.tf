variable "project_id" {
  type        = string
  description = "GCP project ID."
}

variable "region" {
  type        = string
  description = "GCP region for all resources."
  default     = "europe-west2"
}

variable "project_name" {
  type        = string
  description = "Short name used to prefix all resources."
  default     = "fabricate"
}

variable "environment" {
  type        = string
  description = "Deployment environment label."
  default     = "prod"
}

variable "image_tag" {
  type        = string
  description = "Container image tag (the Artifact Registry repo is derived automatically)."
  default     = "latest"
}

variable "db_password" {
  type        = string
  description = "Password for the Cloud SQL PostgreSQL user."
  sensitive   = true
}

variable "bootstrap_api_key" {
  type        = string
  description = "Plaintext bootstrap API key pre-seeded at startup for smoke tests."
  sensitive   = true
}

variable "cloud_run_min_instances" {
  type        = number
  description = "Minimum number of Cloud Run instances (0 = scale to zero)."
  default     = 0
}

variable "cloud_run_max_instances" {
  type        = number
  description = "Maximum number of Cloud Run instances."
  default     = 3
}
