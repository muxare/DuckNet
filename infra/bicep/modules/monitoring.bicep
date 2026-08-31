param location string
param environmentName string

var workspaceName = 'law-ducknet-${environmentName}'
var insightsName = 'ai-ducknet-${environmentName}'

resource workspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: workspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: insightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

output workspaceId string = workspace.id
output workspaceCustomerId string = workspace.properties.customerId
output workspaceName string = workspace.name
output insightsId string = insights.id
@secure()
output insightsConnectionString string = insights.properties.ConnectionString
output insightsName string = insights.name
@secure()
output workspaceSharedKey string = workspace.listKeys().primarySharedKey
