param location string
param environmentName string

// ACR: 5-50 alphanumeric, globally unique.
var acrName = take('ducknet${environmentName}${uniqueString(resourceGroup().id, environmentName)}', 50)

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

output id string = acr.id
output name string = acr.name
output loginServer string = acr.properties.loginServer
