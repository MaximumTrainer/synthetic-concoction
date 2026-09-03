variable "aws_region" {
  type        = string
  description = "AWS region to deploy into."
  default     = "eu-west-2"
}

variable "project_name" {
  type        = string
  description = "Short name used to prefix all resources."
  default     = "fabricate"
}

variable "environment" {
  type        = string
  description = "Deployment environment label (e.g. prod, staging)."
  default     = "prod"
}

variable "image_uri" {
  type        = string
  description = "Full ECR image URI including tag, e.g. 123456789012.dkr.ecr.eu-west-2.amazonaws.com/fabricate:latest"
}

variable "db_password" {
  type        = string
  description = "Password for the RDS PostgreSQL admin user."
  sensitive   = true
}

variable "bootstrap_api_key" {
  type        = string
  description = "Plaintext bootstrap API key pre-seeded at startup for smoke tests."
  sensitive   = true
}

variable "container_cpu" {
  type        = number
  description = "ECS task CPU units (1 vCPU = 1024)."
  default     = 512
}

variable "container_memory" {
  type        = number
  description = "ECS task memory in MiB."
  default     = 1024
}
