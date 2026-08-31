param location string
param environmentName string
param hubName string = 'ducknet-events'
// Hot-partition spec: partition count matches Center shard count in 12c.
// Step 8 local default is 3; Azure hub uses 4 as the locked 12b example.
param partitionCount int = 4

var namespaceName = take('eh-ducknet-${environmentName}-${uniqueString(resourceGroup().id)}', 50)

resource namespace 'Microsoft.EventHub/namespaces@2024-01-01' = {
  name: namespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
    capacity: 1
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource hub 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' = {
  parent: namespace
  name: hubName
  properties: {
    partitionCount: partitionCount
    messageRetentionInDays: 1
  }
}

output namespaceId string = namespace.id
output namespaceName string = namespace.name
output hubName string = hub.name
output hubId string = hub.id
output fqdn string = '${namespace.name}.servicebus.windows.net'
output partitionCount int = partitionCount
