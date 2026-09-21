namespace Dhole.Agent.Domain.Agents;

public enum AgentProviderType { Maersk, Msc, Pil, CmaCgm, HapagLloyd, GenericWeb }
public enum AgentExecutionStrategy { Browser, BrowserNetworkCapture, Hermes, Hybrid }
public enum AgentActionType { SearchOceanRates, Authenticate, GenericExtraction }
public enum BrowserProfileStatus { Unknown, Ready, LoginRequired, Authenticating, Authenticated, Expired, Blocked, Error }
public enum AgentScheduleType { Once, Interval, Cron }
public enum AgentExecutionType { Manual, Scheduled, Api, Grpc }
public enum AgentExecutionStatus { Pending, Queued, Running, WaitingForAuthentication, Completed, PartiallyCompleted, Failed, Cancelled }
public enum AgentEndpointMatchType { Contains, Exact, Regex }
