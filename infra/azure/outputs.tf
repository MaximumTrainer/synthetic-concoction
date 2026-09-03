output "api_url" {
  description = "Public FQDN of the Fabricate API via Container Apps ingress."
  value       = "https://${azurerm_container_app.api.ingress[0].fqdn}"
}

output "acr_login_server" {
  description = "ACR login server for pushing images."
  value       = azurerm_container_registry.main.login_server
}

output "acr_admin_username" {
  description = "ACR admin username."
  value       = azurerm_container_registry.main.admin_username
  sensitive   = true
}

output "postgresql_fqdn" {
  description = "PostgreSQL Flexible Server FQDN (schema-discovery source)."
  value       = azurerm_postgresql_flexible_server.main.fqdn
}
