param location string
param environmentName string
param logAnalyticsCustomerId string
@secure()
param logAnalyticsSharedKey string
param acrLoginServer string
param telemetryIdentityId string
param alarmIdentityId string
param dashboardIdentityId string
param billingIdentityId string
param telemetryClientId string
param alarmClientId string
param dashboardClientId string
param billingClientId string
param serviceBusNamespace string
param serviceBusTopicName string = 'ducknet-events'
param eventHubsNamespace string
param eventHubsHubName string = 'ducknet-events'
param keyVaultUri string
@secure()
param insightsConnectionString string
param placeholderImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

var caeName = 'cae-ducknet-${environmentName}'
var kedaMessageCount = '30'

resource cae 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: caeName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logAnalyticsSharedKey
      }
    }
  }
}

var apps = [
  {
    name: 'telemetry'
    identityId: telemetryIdentityId
    clientId: telemetryClientId
    subscription: ''
  }
  {
    name: 'alarm'
    identityId: alarmIdentityId
    clientId: alarmClientId
    subscription: 'alarm-center'
  }
  {
    name: 'dashboard'
    identityId: dashboardIdentityId
    clientId: dashboardClientId
    subscription: 'dashboard-projector'
  }
  {
    name: 'billing'
    identityId: billingIdentityId
    clientId: billingClientId
    subscription: 'billing-center'
  }
]

resource containerApp 'Microsoft.App/containerApps@2025-01-01' = [
  for app in apps: {
    name: 'ducknet-${app.name}'
    location: location
    identity: {
      type: 'UserAssigned'
      userAssignedIdentities: {
        '${app.identityId}': {}
      }
    }
    properties: {
      managedEnvironmentId: cae.id
      configuration: {
        ingress: {
          external: true
          targetPort: 8080
          allowInsecure: false
        }
        // AcrPull RBAC alone is not used for pulls; the app must name the
        // registry and the identity to authenticate with.
        registries: [
          {
            server: acrLoginServer
            identity: app.identityId
          }
        ]
        secrets: [
          {
            name: 'ai-connection'
            value: insightsConnectionString
          }
        ]
      }
      template: {
        containers: [
          {
            name: app.name
            image: placeholderImage
            resources: {
              cpu: json('0.25')
              memory: '0.5Gi'
            }
            env: [
              {
                name: 'ASPNETCORE_URLS'
                value: 'http://+:8080'
              }
              {
                name: 'AZURE_CLIENT_ID'
                value: app.clientId
              }
              {
                name: 'DUCKNET_ACR'
                value: acrLoginServer
              }
              {
                name: 'DUCKNET_SERVICEBUS_NAMESPACE'
                value: serviceBusNamespace
              }
              {
                name: 'DUCKNET_BUS_TOPIC'
                value: serviceBusTopicName
              }
              {
                name: 'DUCKNET_EVENTHUBS_NAMESPACE'
                value: eventHubsNamespace
              }
              {
                name: 'DUCKNET_EVENTHUBS_HUB'
                value: eventHubsHubName
              }
              {
                name: 'DUCKNET_KEYVAULT_URI'
                value: keyVaultUri
              }
              {
                name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
                secretRef: 'ai-connection'
              }
            ]
          }
        ]
        scale: {
          minReplicas: 1
          maxReplicas: !empty(app.subscription) ? 8 : 1
          rules: !empty(app.subscription)
            ? [
                {
                  name: 'sb-depth'
                  custom: {
                    type: 'azure-servicebus'
                    metadata: {
                      namespace: first(split(serviceBusNamespace, '.'))
                      topicName: serviceBusTopicName
                      subscriptionName: app.subscription
                      messageCount: kedaMessageCount
                    }
                    identity: app.identityId
                  }
                }
              ]
            : []
        }
      }
    }
  }
]

output environmentId string = cae.id
output environmentName string = cae.name
output appNames array = [for app in apps: 'ducknet-${app.name}']
output telemetryFqdn string = containerApp[0].properties.configuration.ingress.fqdn
output alarmFqdn string = containerApp[1].properties.configuration.ingress.fqdn
output dashboardFqdn string = containerApp[2].properties.configuration.ingress.fqdn
output billingFqdn string = containerApp[3].properties.configuration.ingress.fqdn
