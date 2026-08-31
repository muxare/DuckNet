param location string
param environmentName string
param topicName string = 'ducknet-events'

var namespaceName = take('sb-ducknet-${environmentName}-${uniqueString(resourceGroup().id)}', 50)
var subscriptions = ['alarm-center', 'dashboard-projector', 'billing-center']

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: namespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource topic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: namespace
  name: topicName
  properties: {
    defaultMessageTimeToLive: 'P14D'
    maxSizeInMegabytes: 1024
    requiresDuplicateDetection: false
    supportOrdering: false
  }
}

resource subscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = [
  for name in subscriptions: {
    parent: topic
    name: name
    properties: {
      maxDeliveryCount: 10
      deadLetteringOnMessageExpiration: true
      deadLetteringOnFilterEvaluationExceptions: true
      defaultMessageTimeToLive: 'P14D'
      lockDuration: 'PT1M'
    }
  }
]

output namespaceId string = namespace.id
output namespaceName string = namespace.name
output topicName string = topic.name
output topicId string = topic.id
output fqdn string = '${namespace.name}.servicebus.windows.net'
output subscriptionNames array = subscriptions
