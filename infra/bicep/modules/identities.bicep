param location string
param environmentName string

var centers = ['telemetry', 'alarm', 'dashboard', 'billing']

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [
  for center in centers: {
    name: 'id-ducknet-${center}-${environmentName}'
    location: location
  }
]

output ids array = [for (center, i) in centers: identities[i].id]
output clientIds array = [for (center, i) in centers: identities[i].properties.clientId]
output principalIds array = [for (center, i) in centers: identities[i].properties.principalId]
output names array = [for (center, i) in centers: identities[i].name]
output telemetryId string = identities[0].id
output alarmId string = identities[1].id
output dashboardId string = identities[2].id
output billingId string = identities[3].id
output telemetryPrincipalId string = identities[0].properties.principalId
output alarmPrincipalId string = identities[1].properties.principalId
output dashboardPrincipalId string = identities[2].properties.principalId
output billingPrincipalId string = identities[3].properties.principalId
output telemetryClientId string = identities[0].properties.clientId
output alarmClientId string = identities[1].properties.clientId
output dashboardClientId string = identities[2].properties.clientId
output billingClientId string = identities[3].properties.clientId
