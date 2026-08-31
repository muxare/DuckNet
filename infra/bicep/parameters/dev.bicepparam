using '../main.bicep'

param environmentName = 'dev'
param location = 'swedencentral'
param pipelinePrincipalId = ''
param postgresAdminLogin = 'ducknet'
// Placeholder only — never applied in 12b. 12c supplies a real secret at deploy time.
param postgresAdminLoginPassword = 'ChangeMe-at-12c-apply!'
