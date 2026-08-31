param location string
param environmentName string
param tenantId string = tenant().tenantId

var vaultName = take('kv-dn-${environmentName}-${uniqueString(resourceGroup().id)}', 24)

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  properties: {
    tenantId: tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

output id string = vault.id
output name string = vault.name
output uri string = vault.properties.vaultUri
