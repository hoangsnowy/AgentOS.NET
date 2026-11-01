targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment that can be used as part of naming resource convention, the name of the resource group for your application will use this name, prefixed with rg-')
param environmentName string

@minLength(1)
@description('The location used for all deployed resources')
param location string

@description('Id of the user or app to assign application roles')
param principalId string = ''

@secure()
param KeycloakAdminPassword string
param KeycloakAdminUsername string
@secure()
param KeycloakWebClientSecret string
@secure()
param PostgresPassword string
param PostgresUsername string
param SmtpHost string
param SmtpPort string

@description('E1.10: deploy Application Insights, the operations workbook and the alert rules (docs/OPERATIONS.md).')
param enableObservability bool = false

@description('E-mail address for AgentOS.NET alerts (only with enableObservability).')
param alertEmail string = ''

@description('Public /health URL for the availability test, e.g. https://web.<domain>/health (only with enableObservability; empty = none).')
param healthProbeUrl string = ''

var tags = {
  'azd-env-name': environmentName
}

resource rg 'Microsoft.Resources/resourceGroups@2022-09-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}
module resources 'resources.bicep' = {
  scope: rg
  name: 'resources'
  params: {
    location: location
    tags: tags
    principalId: principalId
  }
}

module postgres 'postgres/postgres.module.bicep' = {
  name: 'postgres'
  scope: rg
  params: {
    administratorLogin: PostgresUsername
    administratorLoginPassword: PostgresPassword
    location: location
    postgres_kv_outputs_name: postgres_kv.outputs.name
  }
}
module postgres_kv 'postgres-kv/postgres-kv.module.bicep' = {
  name: 'postgres-kv'
  scope: rg
  params: {
    location: location
  }
}
module postgres_kv_roles 'postgres-kv-roles/postgres-kv-roles.module.bicep' = {
  name: 'postgres-kv-roles'
  scope: rg
  params: {
    location: location
    postgres_kv_outputs_name: postgres_kv.outputs.name
    principalId: resources.outputs.MANAGED_IDENTITY_PRINCIPAL_ID
    principalType: 'ServicePrincipal'
  }
}

module observability 'observability/observability.module.bicep' = if (enableObservability) {
  name: 'observability'
  scope: rg
  params: {
    location: location
    tags: tags
    namePrefix: environmentName
    logAnalyticsWorkspaceId: resources.outputs.AZURE_LOG_ANALYTICS_WORKSPACE_ID
    alertEmail: alertEmail
    healthProbeUrl: healthProbeUrl
  }
}

output MANAGED_IDENTITY_CLIENT_ID string = resources.outputs.MANAGED_IDENTITY_CLIENT_ID
output MANAGED_IDENTITY_NAME string = resources.outputs.MANAGED_IDENTITY_NAME
output AZURE_LOG_ANALYTICS_WORKSPACE_NAME string = resources.outputs.AZURE_LOG_ANALYTICS_WORKSPACE_NAME
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = resources.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = resources.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID
output AZURE_CONTAINER_REGISTRY_NAME string = resources.outputs.AZURE_CONTAINER_REGISTRY_NAME
output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = resources.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_NAME
output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = resources.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID
output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = resources.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN
output POSTGRES_KV_VAULTURI string = postgres_kv.outputs.vaultUri
output POSTGRES_HOSTNAME string = postgres.outputs.hostName
#disable-next-line outputs-should-not-contain-secrets // azd hands it to the hosts as an env var; it is an ingestion key, not a credential to data
output APPLICATIONINSIGHTS_CONNECTION_STRING string = enableObservability ? observability!.outputs.connectionString : ''
