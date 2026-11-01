// E1.10 — AgentOS.NET observability on Azure Monitor: workspace-based Application Insights (the hosts export to it
// when APPLICATIONINSIGHTS_CONNECTION_STRING is set), the operations workbook, an e-mail action group, log-search
// alerts over the app's OpenTelemetry metrics, and an availability test + metric alert on /health.
// Deployed only when main.bicep's `enableObservability` is true. Runbooks: docs/OPERATIONS.md.

@description('Location for the resources.')
param location string

@description('Tags applied to every resource.')
param tags object = {}

@description('Resource id of the Log Analytics workspace Application Insights stores its data in.')
param logAnalyticsWorkspaceId string

@description('Name prefix for the resources (e.g. the azd environment name).')
param namePrefix string

@description('E-mail address that receives the alerts. Empty = alerts are created without a notification target.')
param alertEmail string = ''

@description('Public URL of the Web /health endpoint (e.g. https://web.<domain>/health). Empty = no availability test.')
param healthProbeUrl string = ''

@description('Create the alert rules (disable to deploy only App Insights + the workbook).')
param enableAlerts bool = true

var resourceToken = uniqueString(resourceGroup().id, namePrefix)
var runbookBase = 'https://github.com/hoangsnowy/AgentOS.NET/blob/main/docs/OPERATIONS.md#runbook-'

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${resourceToken}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspaceId
    IngestionMode: 'LogAnalytics'
  }
}

resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: guid(resourceGroup().id, namePrefix, 'agentos-operations-workbook')
  location: location
  tags: tags
  kind: 'shared'
  properties: {
    displayName: 'AgentOS.NET — operations'
    category: 'workbook'
    sourceId: appInsights.id
    serializedData: loadTextContent('../../deploy/observability/azure/agentos-workbook.json')
  }
}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (enableAlerts) {
  name: 'ag-agentos-${resourceToken}'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'agentos'
    enabled: true
    emailReceivers: empty(alertEmail) ? [] : [
      {
        name: 'operators'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// Log-search alerts over customMetrics (OpenTelemetry metrics exported as delta sums) and requests. Each mirrors
// the Prometheus rule of the same name in deploy/observability/prometheus/alerts.yml.
var logAlerts = [
  {
    name: 'AgentOsBudgetOverCap'
    severity: 1
    window: 'PT15M'
    frequency: 'PT5M'
    description: 'A tenant is at or over its monthly LLM budget.'
    // The LATEST sample per tenant, not the window max: removing a cap or a new month reports 0 and must resolve it.
    query: 'customMetrics | where name == "agentos.budget.utilization" | summarize arg_max(timestamp, valueMax) by Tenant = tostring(customDimensions["agentos.tenant.id"]) | where valueMax >= 1'
  }
  {
    name: 'AgentOsFailedRunSpike'
    severity: 1
    window: 'PT30M'
    frequency: 'PT5M'
    description: 'More than 25% of agent runs failed in the last 30 minutes (at least 3).'
    query: 'customMetrics | where name == "agentos.pipeline.runs" | extend Outcome = tostring(customDimensions["agentos.run.outcome"]) | summarize Failed = sumif(valueSum, Outcome == "failed"), Started = sumif(valueSum, Outcome in ("done", "max_iterations", "failed")) | where Failed >= 3 and Failed / Started > 0.25'
  }
  {
    name: 'AgentOsEvidenceLost'
    severity: 0
    window: 'PT10M'
    frequency: 'PT5M'
    description: 'Tool-call evidence rows were lost after every persistence attempt failed.'
    query: 'customMetrics | where name == "agentos.evidence.lost" | summarize Lost = sum(valueSum) by cloud_RoleInstance | where Lost > 0'
  }
  {
    name: 'AgentOsHealthCheckUnhealthy'
    severity: 1
    window: 'PT10M'
    frequency: 'PT5M'
    description: 'A health check (Postgres, Keycloak) reported Unhealthy.'
    query: 'customMetrics | where name == "agentos.health.status" and tostring(customDimensions["agentos.health.check"]) != "overall" | summarize Worst = min(valueMin) by Check = tostring(customDimensions["agentos.health.check"]), cloud_RoleName | where Worst == 0'
  }
  {
    name: 'AgentOsHigh5xxRate'
    severity: 1
    window: 'PT10M'
    frequency: 'PT5M'
    description: 'More than 5% of HTTP requests failed with 5xx.'
    query: 'requests | where name !has "/health" and name !has "/alive" | summarize Total = sum(itemCount), Errors = sumif(itemCount, toint(resultCode) >= 500) by cloud_RoleName | where Total > 60 and todouble(Errors) / Total > 0.05'
  }
  {
    name: 'AgentOsLlmRateLimitStorm'
    severity: 1
    window: 'PT10M'
    frequency: 'PT5M'
    description: 'More than 20% of LLM attempts were rate-limited (HTTP 429).'
    query: 'let limited = customMetrics | where name == "agentos.llm.errors" and tostring(customDimensions["error.type"]) == "rate_limited" | summarize Limited = sum(valueSum) by System = tostring(customDimensions["gen_ai.system"]); let ok = customMetrics | where name == "agentos.llm.calls" | summarize Calls = sum(valueSum) by System = tostring(customDimensions["gen_ai.system"]); limited | join kind=leftouter ok on System | extend Calls = coalesce(Calls, 0.0) | where Limited > 50 and Limited / (Limited + Calls) > 0.2'
  }
]

resource logAlertRules 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = [for a in logAlerts: if (enableAlerts) {
  name: '${a.name}-${resourceToken}'
  location: location
  tags: tags
  properties: {
    displayName: a.name
    description: '${a.description} Runbook: ${runbookBase}${toLower(a.name)}'
    severity: a.severity
    enabled: true
    scopes: [appInsights.id]
    evaluationFrequency: a.frequency
    windowSize: a.window
    autoMitigate: true
    criteria: {
      allOf: [
        {
          query: a.query
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [actionGroup.id]
      customProperties: {
        runbook: '${runbookBase}${toLower(a.name)}'
      }
    }
  }
}]

// Outside-in health probe: a standard availability test on /health from three regions + a metric alert.
resource healthTest 'Microsoft.Insights/webtests@2022-06-15' = if (!empty(healthProbeUrl)) {
  name: 'agentos-health-${resourceToken}'
  location: location
  tags: union(tags, {
    'hidden-link:${appInsights.id}': 'Resource'
  })
  kind: 'standard'
  properties: {
    SyntheticMonitorId: 'agentos-health-${resourceToken}'
    Name: 'AgentOS.NET /health'
    Enabled: true
    Frequency: 300
    Timeout: 30
    Kind: 'standard'
    RetryEnabled: true
    Locations: [
      { Id: 'us-va-ash-azr' }
      { Id: 'emea-nl-ams-azr' }
      { Id: 'apac-sg-sin-azr' }
    ]
    Request: {
      RequestUrl: healthProbeUrl
      HttpVerb: 'GET'
      ParseDependentRequests: false
    }
    ValidationRules: {
      ExpectedHttpStatusCode: 200
      SSLCheck: true
      SSLCertRemainingLifetimeCheck: 7
    }
  }
}

resource healthProbeDown 'Microsoft.Insights/metricAlerts@2018-03-01' = if (enableAlerts && !empty(healthProbeUrl)) {
  name: 'AgentOsHealthProbeDown-${resourceToken}'
  location: 'global'
  tags: tags
  properties: {
    description: 'The /health availability test fails from 2 or more locations. Runbook: ${runbookBase}agentoshealthprobedown'
    severity: 0
    enabled: true
    scopes: [
      healthTest.id
      appInsights.id
    ]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.WebtestLocationAvailabilityCriteria'
      webTestId: healthTest.id
      componentId: appInsights.id
      failedLocationCount: 2
    }
    actions: [
      { actionGroupId: actionGroup.id }
    ]
  }
}

// Platform metric alert on the standard App Insights metric — catches unhandled server exceptions.
resource serverExceptions 'Microsoft.Insights/metricAlerts@2018-03-01' = if (enableAlerts) {
  name: 'AgentOsServerExceptions-${resourceToken}'
  location: 'global'
  tags: tags
  properties: {
    description: 'More than 10 unhandled server exceptions in 5 minutes. Runbook: ${runbookBase}agentoshigh5xxrate'
    severity: 2
    enabled: true
    scopes: [appInsights.id]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'ServerExceptions'
          metricNamespace: 'microsoft.insights/components'
          metricName: 'exceptions/server'
          operator: 'GreaterThan'
          threshold: 10
          timeAggregation: 'Count'
        }
      ]
    }
    actions: [
      { actionGroupId: actionGroup.id }
    ]
  }
}

@description('Connection string the Api + Web read as APPLICATIONINSIGHTS_CONNECTION_STRING.')
output connectionString string = appInsights.properties.ConnectionString
output appInsightsName string = appInsights.name
output workbookId string = workbook.id
