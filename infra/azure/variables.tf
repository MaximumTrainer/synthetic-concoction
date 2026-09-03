variable "location" {
  type        = string
  description = "Azure region for all resources."
  default     = "uksouth"
}

variable "resource_group_name" {
  type        = string
  description = "Name of the Azure Resource Group."
  default     = "rg-fabricate"
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
  description = "Container image tag to deploy (the ACR repo is derived automatically)."
  default     = "latest"
}

variable "db_admin_password" {
  type        = string
  description = "Admin password for Azure Database for PostgreSQL Flexible Server."
  sensitive   = true
}

variable "bootstrap_api_key" {
  type        = string
  description = "Plaintext bootstrap API key pre-seeded at startup for smoke tests."
  sensitive   = true
}

variable "container_cpu" {
  type        = number
  description = "Container CPU allocation in cores."
  default     = 0.5
}

variable "container_memory" {
  type        = string
  description = "Container memory allocation (e.g. '1Gi')."
  default     = "1Gi"
}
