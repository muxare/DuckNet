targetScope = 'resourceGroup'

@description('Environment short name. Maps to GitHub Environment azure-dev / azure-prod.')
@allowed([
  'dev'
  'prod'
])
param environmentName string

@description('Azure region. Default Sweden Central; 12c falls back to West Europe if a SKU is missing.')
param location string = 'swedencentral'

@description('Entra app object id for ducknet-gha (pipeline plane). Empty until 12c bootstrap.')
param pipelinePrincipalId string = ''

@description('PostgreSQL admin login. Not azure_superuser / admin / root.')
param postgresAdminLogin string = 'ducknet'

@secure()
@description('PostgreSQL admin password. Set at 12c apply; placeholder is fine for compile.')
param postgresAdminLoginPassword string

@description('Placeholder Container App image until 12c pushes ACR tags.')
param placeholderImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    environmentName: environmentName
  }
}

module acr 'modules/acr.bicep' = {
  name: 'acr'
  params: {
    location: location
    environmentName: environmentName
  }
}

module identities 'modules/identities.bicep' = {
  name: 'identities'
  params: {
    location: location
    environmentName: environmentName
  }
}

module keyvault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location: location
    environmentName: environmentName
  }
}

module postgres 'modules/postgres.bicep' = {
  name: 'postgres'
  params: {
    location: location
    environmentName: environmentName
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminLoginPassword
  }
}

module eventhubs 'modules/eventhubs.bicep' = {
  name: 'eventhubs'
  params: {
    location: location
    environmentName: environmentName
  }
}

module servicebus 'modules/servicebus.bicep' = {
  name: 'servicebus'
  params: {
    location: location
    environmentName: environmentName
  }
}

module roles 'modules/roles.bicep' = {
  name: 'roles'
  params: {
    acrId: acr.outputs.id
    keyVaultId: keyvault.outputs.id
    eventHubsNamespaceId: eventhubs.outputs.namespaceId
    serviceBusNamespaceId: servicebus.outputs.namespaceId
    pipelinePrincipalId: pipelinePrincipalId
    telemetryPrincipalId: identities.outputs.telemetryPrincipalId
    alarmPrincipalId: identities.outputs.alarmPrincipalId
    dashboardPrincipalId: identities.outputs.dashboardPrincipalId
    billingPrincipalId: identities.outputs.billingPrincipalId
  }
}

module containerapps 'modules/containerapps.bicep' = {
  name: 'containerapps'
  params: {
    location: location
    environmentName: environmentName
    logAnalyticsCustomerId: monitoring.outputs.workspaceCustomerId
    logAnalyticsSharedKey: monitoring.outputs.workspaceSharedKey
    acrLoginServer: acr.outputs.loginServer
    telemetryIdentityId: identities.outputs.telemetryId
    alarmIdentityId: identities.outputs.alarmId
    dashboardIdentityId: identities.outputs.dashboardId
    billingIdentityId: identities.outputs.billingId
    telemetryClientId: identities.outputs.telemetryClientId
    alarmClientId: identities.outputs.alarmClientId
    dashboardClientId: identities.outputs.dashboardClientId
    billingClientId: identities.outputs.billingClientId
    serviceBusNamespace: servicebus.outputs.fqdn
    eventHubsNamespace: eventhubs.outputs.fqdn
    keyVaultUri: keyvault.outputs.uri
    insightsConnectionString: monitoring.outputs.insightsConnectionString
    placeholderImage: placeholderImage
  }
}

output acrLoginServer string = acr.outputs.loginServer
output acrName string = acr.outputs.name
output keyVaultUri string = keyvault.outputs.uri
output postgresFqdn string = postgres.outputs.fqdn
output serviceBusFqdn string = servicebus.outputs.fqdn
output eventHubsFqdn string = eventhubs.outputs.fqdn
output eventHubsPartitionCount int = eventhubs.outputs.partitionCount
output telemetryFqdn string = containerapps.outputs.telemetryFqdn
output alarmFqdn string = containerapps.outputs.alarmFqdn
output dashboardFqdn string = containerapps.outputs.dashboardFqdn
output billingFqdn string = containerapps.outputs.billingFqdn
output appNames array = containerapps.outputs.appNames
