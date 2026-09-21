namespace Dhole.Agent.Api.Authorization;

internal static class AgentScopeNames
{
    public const string ProvidersView = "agent.providers.view";
    public const string ProvidersManage = "agent.providers.manage";
    public const string DefinitionsView = "agent.definitions.view";
    public const string DefinitionsManage = "agent.definitions.manage";
    public const string CredentialsView = "agent.credentials.view";
    public const string CredentialsManage = "agent.credentials.manage";
    public const string CredentialsVerify = "agent.credentials.verify";
    public const string RoutesManage = "agent.routes.manage";
    public const string EquipmentManage = "agent.equipment.manage";
    public const string CaptureRulesManage = "agent.capture-rules.manage";
    public const string ExtractionFieldsManage = "agent.extraction-fields.manage";
    public const string BrowserProfilesView = "agent.browser-profiles.view";
    public const string BrowserProfilesAuthenticate = "agent.browser-profiles.authenticate";
    public const string SchedulesView = "agent.schedules.view";
    public const string SchedulesCreate = "agent.schedules.create";
    public const string SchedulesUpdate = "agent.schedules.update";
    public const string SchedulesDelete = "agent.schedules.delete";
    public const string SchedulesExecute = "agent.schedules.execute";
    public const string ExecutionsView = "agent.executions.view";
    public const string ExecutionsCreate = "agent.executions.create";
    public const string ExecutionsCancel = "agent.executions.cancel";
}
