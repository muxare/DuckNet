param acrId string
param keyVaultId string
param eventHubsNamespaceId string
param serviceBusNamespaceId string
param pipelinePrincipalId string = ''
param telemetryPrincipalId string
param alarmPrincipalId string
param dashboardPrincipalId string
param billingPrincipalId string

// Well-known Azure RBAC role definition IDs.
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  acrPush: '8311e382-0749-4cb8-b61a-304f252e45ec'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
  eventHubsDataSender: '2b629674-e913-4c5a-a23b-98fad2d50fb8'
  eventHubsDataReceiver: 'a638d3c7-ab3a-456d-8c86-2d89dd191712'
  serviceBusDataSender: '69a75ac1-6673-486f-a8d1-87dc2e89e890'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-8842-2d005c82dd11'
}

var centers = [
  {
    name: 'telemetry'
    principalId: telemetryPrincipalId
  }
  {
    name: 'alarm'
    principalId: alarmPrincipalId
  }
  {
    name: 'dashboard'
    principalId: dashboardPrincipalId
  }
  {
    name: 'billing'
    principalId: billingPrincipalId
  }
]

resource kvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for center in centers: {
    name: guid(keyVaultId, center.principalId, roles.keyVaultSecretsUser)
    scope: kv
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
      principalId: center.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for center in centers: {
    name: guid(acrId, center.principalId, roles.acrPull)
    scope: acr
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
      principalId: center.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource ehSenderTelemetry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(eventHubsNamespaceId, telemetryPrincipalId, roles.eventHubsDataSender)
  scope: eh
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.eventHubsDataSender)
    principalId: telemetryPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource ehReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for center in centers: {
    name: guid(eventHubsNamespaceId, center.principalId, roles.eventHubsDataReceiver)
    scope: eh
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.eventHubsDataReceiver)
      principalId: center.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource sbSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for center in centers: {
    name: guid(serviceBusNamespaceId, center.principalId, roles.serviceBusDataSender)
    scope: sb
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
      principalId: center.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource sbReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for center in centers: {
    name: guid(serviceBusNamespaceId, center.principalId, roles.serviceBusDataReceiver)
    scope: sb
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
      principalId: center.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource acrPushPipeline 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(pipelinePrincipalId)) {
  name: guid(acrId, pipelinePrincipalId, roles.acrPush)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPush)
    principalId: pipelinePrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource kvOfficerPipeline 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(pipelinePrincipalId)) {
  name: guid(keyVaultId, pipelinePrincipalId, roles.keyVaultSecretsOfficer)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsOfficer)
    principalId: pipelinePrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: last(split(keyVaultId, '/'))
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: last(split(acrId, '/'))
}

resource eh 'Microsoft.EventHub/namespaces@2024-01-01' existing = {
  name: last(split(eventHubsNamespaceId, '/'))
}

resource sb 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: last(split(serviceBusNamespaceId, '/'))
}

