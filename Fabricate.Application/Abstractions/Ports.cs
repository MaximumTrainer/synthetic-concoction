using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Abstractions;

public interface ISchemaProvider
{
    string ProviderName { get; }
    Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default);
}

public interface ISchemaDiscoveryService
{
    Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Profiles a live database by collecting aggregate-only statistics.
/// Never reads raw row values — only COUNT, DISTINCT, MIN, MAX aggregates.
/// </summary>
public interface IDataProfiler
{
    Task<ProfileSnapshot> ProfileAsync(DatabaseSchema schema, CancellationToken cancellationToken = default);
}

public sealed record GeneratorContext(
    string Table,
    string Column,
    DataKind DataKind,
    int RowIndex,
    RuleConfiguration? Rules,
    IReadOnlyDictionary<string, object?> CurrentRow,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReferencePool);

public interface IValueGenerator<in TContext, TValue>
{
    ValueTask<TValue> GenerateAsync(TContext context, CancellationToken cancellationToken = default);
}

public interface IValueGeneratorDispatcher
{
    ValueTask<object?> GenerateAsync(GeneratorContext context, CancellationToken cancellationToken = default);
}

public interface IGeneratorRegistry
{
    void Register(DataKind kind, Func<GeneratorContext, CancellationToken, ValueTask<object?>> generator, string? strategy = null);
    bool TryResolve(DataKind kind, string? strategy, out Func<GeneratorContext, CancellationToken, ValueTask<object?>> generator);
}

public interface IRandomService
{
    int NextInt(string scope, int minInclusive, int maxExclusive);
    long NextLong(string scope, long minInclusive, long maxExclusive);
    double NextDouble(string scope);
    string NextToken(string scope, int length);
    Guid NextGuid(string scope);
}

public interface IConstraintEvaluator
{
    IReadOnlyList<ValidationIssue> Evaluate(TableSchema table, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows);
}

public interface IGenerationPlanner
{
    GenerationPlan BuildPlan(DatabaseSchema schema);
}

public interface IRowMaterializer
{
    Task<TableData> MaterializeAsync(
        TableSchema table,
        int rowCount,
        RuleConfiguration? rules,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> keyPool,
        CancellationToken cancellationToken = default);
}

/// <summary>Streaming variant of <see cref="IRowMaterializer"/> that yields rows one at a time via IAsyncEnumerable.</summary>
public interface IRowMaterializerStream
{
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamAsync(
        TableSchema table,
        int rowCount,
        RuleConfiguration? rules,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> keyPool,
        CancellationToken cancellationToken = default);
}

public interface IExporter
{
    string Name { get; }
    Task ExportAsync(IReadOnlyList<TableData> tables, string target, CancellationToken cancellationToken = default);
}

/// <summary>
/// Streaming exporter that writes rows incrementally as they are produced.
/// Implementors must be stateful: call <see cref="BeginTableAsync"/> before any <see cref="WriteRowAsync"/> calls.
/// </summary>
public interface IStreamingExporter : IExporter
{
    Task BeginTableAsync(TableSchema table, string target, CancellationToken cancellationToken = default);
    Task WriteRowAsync(IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default);
    Task EndTableAsync(CancellationToken cancellationToken = default);
}

public interface ISensitiveFieldPolicy
{
    ComplianceDecision Evaluate(string table, ColumnSchema column, ComplianceProfile profile = ComplianceProfile.Default);
}

public interface IRuleConfigurationService
{
    RuleConfiguration Load(string path);
    IReadOnlyList<string> Validate(RuleConfiguration configuration);
    RuleConfiguration Merge(RuleConfiguration defaults, RuleConfiguration schemaDerived, RuleConfiguration user);
}

public interface ISyntheticDataOrchestrator
{
    Task<(GenerationResult Result, RunSummary Summary)> GenerateAsync(GenerationRequest request, CancellationToken cancellationToken = default);
    Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming generation path: rows are produced and forwarded to the exporter incrementally.
    /// Only PK key values are buffered per table — full row data is never held in memory.
    /// </summary>
    Task<RunSummary> GenerateStreamingAsync(GenerationRequest request, IStreamingExporter exporter, string target, CancellationToken cancellationToken = default);
}

// ── #13: Schema profiling ports ───────────────────────────────────────────────

public interface ISchemaProfiler
{
    Task<ProfileSnapshot> ProfileAsync(DatabaseSchema schema, CancellationToken cancellationToken = default);
}

public interface IProfileSnapshotRepository
{
    Task SaveAsync(ProfileSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<ProfileSnapshot?> GetLatestAsync(string databaseName, CancellationToken cancellationToken = default);
    Task<ProfileSnapshot?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ISchemaSnapshotRepository
{
    Task SaveAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<SchemaSnapshot?> GetLatestAsync(string databaseName, CancellationToken cancellationToken = default);
    Task<SchemaSnapshot?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ISchemaReviewService
{
    SchemaReviewReport Review(DatabaseSchema schema);
}

// ── #14: Strategy registry and plan engine ports ──────────────────────────────

public sealed record StrategyOverride(string? GlobalStrategy, IReadOnlyDictionary<string, string>? TableStrategies, IReadOnlyDictionary<string, string>? ColumnStrategies);

public sealed record ColumnPlanEntry(string Table, string Column, DataKind DataKind, string ResolvedStrategy, string StrategyProvenance);

public sealed record PlanDiagnosticsReport(IReadOnlyList<ColumnPlanEntry> Columns, IReadOnlyList<string> Warnings);

public interface IStrategyRegistry
{
    void Register(string strategyName, DataKind kind, Func<GeneratorContext, CancellationToken, ValueTask<object?>> generator);
    bool TryResolve(string strategyName, DataKind kind, out Func<GeneratorContext, CancellationToken, ValueTask<object?>> generator);
    IReadOnlyList<string> GetRegisteredStrategies(DataKind kind);
}

public interface IGenerationPlanService
{
    PlanDiagnosticsReport BuildDiagnosticsReport(DatabaseSchema schema, RuleConfiguration? rules = null);
}

// ── #22: Run management ports ─────────────────────────────────────────────────

public interface IRunRepository
{
    Task<DatasetRun> CreateAsync(DatasetRun run, CancellationToken cancellationToken = default);
    Task<DatasetRun> UpdateAsync(DatasetRun run, CancellationToken cancellationToken = default);
    Task<DatasetRun?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatasetRun>> ListAsync(int pageSize = 20, int page = 1, CancellationToken cancellationToken = default);
}

public interface IArtifactStore
{
    Task<string> StoreAsync(string runId, string name, Stream content, CancellationToken cancellationToken = default);
    Task<Stream> RetrieveAsync(string path, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);
}

// ── #26: Account foundation ports ────────────────────────────────────────────

public sealed record CreateAccountCommand(string Name, Guid OwnerId);
public sealed record InviteUserCommand(Guid AccountId, Guid InvitedByUserId, string InviteeEmail, TimeSpan Expiry);
public sealed record AcceptInvitationCommand(string Token, Guid UserId);
public sealed record RevokeInvitationCommand(Guid InvitationId, Guid RequestingUserId);
public sealed record UpdateProfileCommand(Guid UserId, string DisplayName);

public interface IAccountService
{
    Task<Account> CreateAccountAsync(CreateAccountCommand command, CancellationToken cancellationToken = default);
    Task<Account?> GetByIdAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountMembership>> GetMembersAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task EnsureMemberAsync(Guid accountId, Guid userId, CancellationToken cancellationToken = default);
}

public interface IInvitationService
{
    Task<Invitation> InviteAsync(InviteUserCommand command, CancellationToken cancellationToken = default);
    Task<AccountMembership> AcceptAsync(AcceptInvitationCommand command, CancellationToken cancellationToken = default);
    Task RevokeAsync(RevokeInvitationCommand command, CancellationToken cancellationToken = default);
}

public interface IUserProfileService
{
    Task<UserProfile> GetOrCreateAsync(Guid userId, string email, string displayName, CancellationToken cancellationToken = default);
    Task<UserProfile> UpdateAsync(UpdateProfileCommand command, CancellationToken cancellationToken = default);
    Task<UserProfile?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default);
}

public interface IAccountRepository
{
    Task<Account> SaveAsync(Account account, CancellationToken cancellationToken = default);
    Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<AccountMembership> AddMemberAsync(AccountMembership membership, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountMembership>> GetMembersAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<AccountMembership?> GetMembershipAsync(Guid accountId, Guid userId, CancellationToken cancellationToken = default);
}

public interface IUserRepository
{
    Task<UserProfile> SaveAsync(UserProfile profile, CancellationToken cancellationToken = default);
    Task<UserProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<UserProfile?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<Invitation> SaveInvitationAsync(Invitation invitation, CancellationToken cancellationToken = default);
    Task<Invitation?> GetInvitationByTokenAsync(string token, CancellationToken cancellationToken = default);
    Task<Invitation?> GetInvitationByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

// ── #27: Governance ports ─────────────────────────────────────────────────────

public interface IAccountGroupService
{
    Task<AccountGroup> CreateGroupAsync(Guid accountId, string name, Guid createdByUserId, CancellationToken cancellationToken = default);
    Task AddGroupMemberAsync(Guid groupId, Guid userId, Guid requestingUserId, Guid accountId, CancellationToken cancellationToken = default);
    Task RemoveGroupMemberAsync(Guid groupId, Guid userId, Guid requestingUserId, Guid accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountGroup>> ListGroupsAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public interface IAllowedDomainService
{
    Task<AllowedDomain> AddDomainAsync(Guid accountId, string domain, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task RemoveDomainAsync(Guid domainId, Guid requestingUserId, Guid accountId, CancellationToken cancellationToken = default);
    Task<bool> IsEmailAllowedAsync(Guid accountId, string email, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AllowedDomain>> ListDomainsAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public sealed record AuditPage(IReadOnlyList<AuditEvent> Events, int TotalCount, int Page, int PageSize);

public interface IAuditLogService
{
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
    Task<AuditPage> QueryAsync(Guid accountId, int page = 1, int pageSize = 50, string? actionFilter = null, CancellationToken cancellationToken = default);
}

public interface IAuditLogRepository
{
    Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditEvent>> QueryAsync(Guid accountId, int skip, int take, string? actionFilter, CancellationToken cancellationToken = default);
    Task<int> CountAsync(Guid accountId, string? actionFilter, CancellationToken cancellationToken = default);
}

// ── #28: Workspace ports ──────────────────────────────────────────────────────

public sealed record CreateWorkspaceCommand(Guid AccountId, string Name, Guid CreatedByUserId);
public sealed record GrantWorkspaceAccessCommand(Guid WorkspaceId, Guid PrincipalId, bool IsGroup, WorkspaceRole Role, Guid RequestingUserId);

public interface IWorkspaceService
{
    Task<Workspace> CreateAsync(CreateWorkspaceCommand command, CancellationToken cancellationToken = default);
    Task<Workspace?> GetByIdAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task GrantAccessAsync(GrantWorkspaceAccessCommand command, CancellationToken cancellationToken = default);
    Task RevokeAccessAsync(Guid workspaceId, Guid principalId, bool isGroup, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<WorkspaceRole?> GetEffectiveRoleAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default);
}

public interface IConnectionCatalogService
{
    Task<Connection> AddConnectionAsync(Guid workspaceId, string name, string provider, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<Connection> UpdateStatusAsync(Guid connectionId, string status, Guid requestingUserId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task RemoveConnectionAsync(Guid connectionId, Guid requestingUserId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Connection>> ListAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default);
}

public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretName, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string secretName, CancellationToken cancellationToken = default);
}

public interface IInstructionVersionService
{
    Task<InstructionVersion> SaveAsync(Guid workspaceId, string content, Guid createdByUserId, CancellationToken cancellationToken = default);
    Task<InstructionVersion?> GetLatestAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstructionVersion>> GetHistoryAsync(Guid workspaceId, int pageSize = 20, CancellationToken cancellationToken = default);

    /// <summary>Saves a project-level instruction version.</summary>
    Task<InstructionVersion> SaveProjectInstructionAsync(Guid projectId, string content, Guid createdByUserId, CancellationToken cancellationToken = default);
    /// <summary>Returns the latest instruction version for a project, or null if none.</summary>
    Task<InstructionVersion?> GetLatestProjectInstructionAsync(Guid projectId, CancellationToken cancellationToken = default);
}

// ── #29: Project ports ────────────────────────────────────────────────────────

public interface IProjectRepository
{
    Task<Project> SaveAsync(Project project, CancellationToken cancellationToken = default);
    Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Project>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

// ── #65: repositories for the remaining platform aggregates ──────────────────
// These aggregates previously lived in List<> fields inside their Application services, so they were lost on
// restart even with a database configured. Every one now has an in-memory and an EF adapter.

public interface IWorkspaceRepository
{
    Task<Workspace> SaveAsync(Workspace workspace, CancellationToken cancellationToken = default);
    Task<Workspace?> GetByIdAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Workspace>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<WorkspaceMembership> SaveMembershipAsync(WorkspaceMembership membership, CancellationToken cancellationToken = default);
    Task RemoveMembershipAsync(Guid workspaceId, Guid principalId, bool isGroup, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkspaceMembership>> ListMembershipsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

public interface IConnectionRepository
{
    Task<Connection> SaveAsync(Connection connection, CancellationToken cancellationToken = default);
    Task<Connection?> GetByIdAsync(Guid connectionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Connection>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid connectionId, CancellationToken cancellationToken = default);
}

public interface IInstructionVersionRepository
{
    Task<InstructionVersion> SaveAsync(InstructionVersion version, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstructionVersion>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstructionVersion>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
}

public interface IProjectDatabaseRepository
{
    Task<ProjectDatabase> SaveAsync(ProjectDatabase database, CancellationToken cancellationToken = default);
    Task<ProjectDatabase?> GetByIdAsync(Guid databaseId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectDatabase>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid databaseId, CancellationToken cancellationToken = default);
}

public interface IWorkflowRepository
{
    Task<Workflow> SaveAsync(Workflow workflow, CancellationToken cancellationToken = default);
    Task<Workflow?> GetByIdAsync(Guid workflowId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Workflow>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<WorkflowStep> SaveStepAsync(WorkflowStep step, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowStep>> ListStepsAsync(Guid workflowId, CancellationToken cancellationToken = default);
    Task<WorkflowRun> SaveRunAsync(WorkflowRun run, CancellationToken cancellationToken = default);
    Task<WorkflowRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<WorkflowStepRun> SaveStepRunAsync(WorkflowStepRun stepRun, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowStepRun>> ListStepRunsAsync(Guid runId, CancellationToken cancellationToken = default);
}

public interface ISkillRepository
{
    Task<Skill> SaveAsync(Skill skill, CancellationToken cancellationToken = default);
    Task<Skill?> GetByIdAsync(Guid skillId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Skill>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

public interface IAccountGroupRepository
{
    Task<AccountGroup> SaveAsync(AccountGroup group, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountGroup>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<GroupMembership> AddMemberAsync(GroupMembership membership, CancellationToken cancellationToken = default);
    Task RemoveMemberAsync(Guid groupId, Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Groups the user belongs to, across the whole instance; callers scope by account.</summary>
    Task<IReadOnlyList<Guid>> ListGroupIdsForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}

public interface IAllowedDomainRepository
{
    Task<AllowedDomain> SaveAsync(AllowedDomain domain, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AllowedDomain>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid domainId, CancellationToken cancellationToken = default);
}

public sealed record CreateProjectCommand(Guid WorkspaceId, string Name, Guid CreatedByUserId);
public sealed record AddDatabaseCommand(Guid ProjectId, string Name, ProjectDatabaseType Type, string Provider, Guid? ConnectionRefId, Guid RequestingUserId);

public interface IProjectService
{
    Task<Project> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken = default);
    Task<Project> RenameAsync(Guid projectId, string newName, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<Project> ArchiveAsync(Guid projectId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<Project?> GetByIdAsync(Guid projectId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Project>> ListAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default);
}

public interface IProjectDatabaseCatalog
{
    Task<ProjectDatabase> AddAsync(AddDatabaseCommand command, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectDatabase>> ListAsync(Guid projectId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid databaseId, Guid requestingUserId, CancellationToken cancellationToken = default);
}

// ── #30: Agent chat ports ─────────────────────────────────────────────────────

public interface ITool
{
    string Name { get; }
    string Description { get; }

    /// <summary>JSON Schema for the tool's input object, advertised to the model. Defaults to an open object.</summary>
    string InputSchemaJson => """{"type":"object","properties":{},"additionalProperties":true}""";

    Task<string> ExecuteAsync(string inputJson, Guid sessionId, Guid userId, CancellationToken cancellationToken = default);
}

public interface IToolRegistry
{
    void Register(ITool tool);
    ITool? Resolve(string toolName);

    /// <summary>Tools the given workspace may invoke. Every registered tool unless an allowlist has been set.</summary>
    IReadOnlyList<string> AllowedTools(Guid workspaceId);

    /// <summary>Restricts a workspace to the named tools. Unknown names are ignored.</summary>
    void SetAllowedTools(Guid workspaceId, IReadOnlyList<string> toolNames);
}

public sealed record CreateChatSessionCommand(Guid WorkspaceId, Guid? ProjectId, Guid UserId, string Name, ChatMode Mode = ChatMode.Guided);
public sealed record SendMessageCommand(Guid SessionId, Guid UserId, string Content);

/// <summary>Everything one user message produced: the persisted user turn, the model's reply (if any), and tool activity.</summary>
public sealed record ChatTurnResult(
    ChatMessage UserMessage,
    ChatMessage? AssistantMessage,
    IReadOnlyList<ToolInvocation> ToolInvocations,
    TokenUsage Usage,
    LlmStopReason? StopReason);

/// <summary>Outcome of approving a parked tool call. <see cref="Continuation"/> is set when the approval unblocked the model loop.</summary>
public sealed record ToolApprovalResult(ToolInvocation Invocation, ChatTurnResult? Continuation);

/// <summary>Estimates the input-token cost of a request so history can be trimmed to a budget before it is sent.</summary>
public interface ITokenBudgetEstimator
{
    int Estimate(ChatCompletionRequest request);
    int Estimate(LlmMessage message);
}

/// <summary>Incremental events emitted while a turn streams.</summary>
public abstract record ChatStreamEvent
{
    public sealed record TextDelta(string Text) : ChatStreamEvent;
    public sealed record ToolCallRequested(ToolInvocation Invocation) : ChatStreamEvent;
    public sealed record ToolCompleted(ToolInvocation Invocation) : ChatStreamEvent;
    public sealed record Notice(string Message) : ChatStreamEvent;
    public sealed record Completed(ChatTurnResult Result) : ChatStreamEvent;
}

public interface IAgentChatService
{
    Task<ChatSession> CreateSessionAsync(CreateChatSessionCommand command, CancellationToken cancellationToken = default);
    Task<ChatSession?> GetSessionAsync(Guid sessionId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<ChatSession> ArchiveSessionAsync(Guid sessionId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<ChatSession> ChangeMode(Guid sessionId, ChatMode mode, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<ChatSession> SetInstructionOverrideAsync(Guid sessionId, string? instructionOverride, Guid requestingUserId, CancellationToken cancellationToken = default);

    /// <summary>Persists the user message, runs the model/tool loop to completion, and returns the whole turn.</summary>
    Task<ChatTurnResult> SendMessageAsync(SendMessageCommand command, CancellationToken cancellationToken = default);

    /// <summary>Same as <see cref="SendMessageAsync"/> but yields incremental events; the last event is always <see cref="ChatStreamEvent.Completed"/>.</summary>
    IAsyncEnumerable<ChatStreamEvent> StreamMessageAsync(SendMessageCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a tool call that was parked as <see cref="ToolInvocationStatus.Pending"/> under <see cref="ChatMode.ReviewRequired"/>.
    /// Once no call in the session is pending, the model loop resumes with the tool results and the resulting turn is returned as
    /// <see cref="ToolApprovalResult.Continuation"/>.
    /// </summary>
    Task<ToolApprovalResult> ApproveToolInvocationAsync(Guid sessionId, Guid invocationId, Guid requestingUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(Guid sessionId, Guid requestingUserId, int pageSize = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolInvocation>> GetToolInvocationsAsync(Guid sessionId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<string> GetComposedInstructionsAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public interface ISessionRepository
{
    Task<ChatSession> SaveAsync(ChatSession session, CancellationToken cancellationToken = default);
    Task<ChatSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ChatMessage> SaveMessageAsync(ChatMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid sessionId, int skip, int take, CancellationToken cancellationToken = default);
    Task<ToolInvocation> SaveInvocationAsync(ToolInvocation invocation, CancellationToken cancellationToken = default);
    Task<ToolInvocation?> GetInvocationAsync(Guid invocationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ToolInvocation>> ListInvocationsAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

// ── #31: API key ports ────────────────────────────────────────────────────────

public sealed record CreateApiKeyCommand(Guid AccountId, string Name, IReadOnlyList<string> Scopes, TimeSpan? Expiry = null);

public interface IApiKeyService
{
    /// <summary>Creates a new API key. Returns the record and the plaintext secret (only visible once).</summary>
    Task<(ApiKey Key, string PlaintextSecret)> CreateAsync(CreateApiKeyCommand command, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<ApiKey> RevokeAsync(Guid keyId, Guid requestingUserId, Guid accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApiKey>> ListAsync(Guid accountId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<ApiKey?> ValidateAsync(string plaintextSecret, CancellationToken cancellationToken = default);
}

public interface IApiKeyStore
{
    Task<ApiKey> SaveAsync(ApiKey key, CancellationToken cancellationToken = default);
    Task<ApiKey?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApiKey>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<ApiKey?> FindByHashAsync(string hashedSecret, CancellationToken cancellationToken = default);
    Task<ApiKey> UpdateAsync(ApiKey key, CancellationToken cancellationToken = default);
}

// ── #24: Workflow ports ───────────────────────────────────────────────────────

public sealed record CreateWorkflowCommand(Guid WorkspaceId, string Name, IReadOnlyList<WorkflowStepDefinition> Steps);
public sealed record WorkflowStepDefinition(int StepOrder, string StepType, string? Configuration);

public interface IWorkflowService
{
    Task<Workflow> CreateAsync(CreateWorkflowCommand command, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<WorkflowRun> RunAsync(Guid workflowId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<WorkflowRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowStepRun>> GetStepRunsAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<Workflow> DisableAsync(Guid workflowId, Guid requestingUserId, CancellationToken cancellationToken = default);
}

public interface ISkillRegistry
{
    Task RegisterSkillAsync(Skill skill, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<Skill?> GetSkillAsync(Guid skillId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Skill>> ListSkillsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<bool> IsToolAllowedAsync(Guid skillId, string toolName, CancellationToken cancellationToken = default);
}

public interface IApiContractIngestionService
{
    Task<IReadOnlyList<GeneratedApiEndpoint>> IngestAsync(string openApiJson, Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default);
}

public interface ISchemaSnapshotService
{
    Task<SchemaSnapshot> SaveSnapshotAsync(Guid workspaceId, DatabaseSchema schema, CancellationToken cancellationToken = default);
    Task<SchemaSnapshot?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SchemaSnapshot>> ListSnapshotsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<DatabaseSchema?> RestoreSchemaAsync(Guid snapshotId, CancellationToken cancellationToken = default);
}

public interface IProfileSnapshotService
{
    Task<ProfileSnapshot> SaveProfileAsync(Guid workspaceId, ProfileSnapshot profile, CancellationToken cancellationToken = default);
    Task<ProfileSnapshot?> GetProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProfileSnapshot>> ListProfilesAsync(Guid workspaceId, CancellationToken cancellationToken = default);
}

// ── #43: Webhook ports ────────────────────────────────────────────────────────

public sealed record RegisterWebhookCommand(Guid WorkspaceId, string Url, IReadOnlyList<string> Events, string? SigningSecret = null);

public interface IWebhookService
{
    Task<WebhookRegistration> RegisterAsync(RegisterWebhookCommand command, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<WebhookRegistration?> GetAsync(Guid webhookId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebhookRegistration>> ListAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid webhookId, Guid requestingUserId, CancellationToken cancellationToken = default);
}

public interface IWebhookDeliveryService
{
    /// <summary>Delivers an event to all active webhooks subscribed to it for the given workspace.</summary>
    Task DeliverAsync(Guid workspaceId, string eventName, object payload, CancellationToken cancellationToken = default);
}

public interface IWebhookRepository
{
    Task<WebhookRegistration> SaveAsync(WebhookRegistration webhook, CancellationToken cancellationToken = default);
    Task<WebhookRegistration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebhookRegistration>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<WebhookDelivery> SaveDeliveryAsync(WebhookDelivery delivery, CancellationToken cancellationToken = default);
}

// ── #52: NoSQL / document-database provider ports ────────────────────────────

/// <summary>
/// Discovers the collection metadata (schema) of a document/NoSQL database.
/// Analogous to <see cref="ISchemaProvider"/> for relational databases.
/// </summary>
public interface INoSqlSchemaDiscoverer
{
    /// <summary>Identifies the provider, e.g. "cosmosdb", "mongodb", "dynamodb", "firestore".</summary>
    string ProviderName { get; }

    /// <summary>
    /// Returns canonical metadata for all discoverable collections in the target database.
    /// Implementations must sample documents to infer field types; they must not return raw document content.
    /// </summary>
    Task<IReadOnlyList<CollectionMetadata>> DiscoverCollectionsAsync(
        string connectionString,
        string databaseName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Profiles a document/NoSQL database by collecting aggregate-only statistics.
/// No raw document content is read — only COUNT, DISTINCT, MIN, MAX aggregates.
/// </summary>
public interface INoSqlDataProfiler
{
    /// <summary>Identifies the provider, e.g. "cosmosdb", "mongodb", "dynamodb", "firestore".</summary>
    string ProviderName { get; }

    /// <summary>
    /// Returns a <see cref="NoSqlProfileSnapshot"/> with per-collection and per-field statistics.
    /// </summary>
    Task<NoSqlProfileSnapshot> ProfileAsync(
        IReadOnlyList<CollectionMetadata> collections,
        string connectionString,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the appropriate <see cref="INoSqlSchemaDiscoverer"/> for a given provider name.
/// </summary>
public interface INoSqlSchemaDiscovererFactory
{
    INoSqlSchemaDiscoverer GetDiscoverer(string providerName);
}

/// <summary>
/// Resolves the appropriate <see cref="INoSqlDataProfiler"/> for a given provider name.
/// </summary>
public interface INoSqlDataProfilerFactory
{
    INoSqlDataProfiler GetProfiler(string providerName);
}

// ── #46/#47/#58/#60: LLM provider and bring-your-own-key ports ───────────────

/// <summary>A configured, authenticated connection to one model provider.</summary>
public interface IChatCompletionClient
{
    string ProviderId { get; }
    ModelCapabilities Capabilities { get; }
    Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Builds a client for a resolved credential. Implemented in Infrastructure; the only place vendor SDKs are referenced.</summary>
public interface IChatCompletionClientFactory
{
    IChatCompletionClient Create(Llm.ResolvedLlmCredential credential);
}

/// <summary>Resolves which credential a chat turn executes under. Returns <c>null</c> when none is configured.</summary>
public interface ILlmCredentialResolver
{
    Task<Llm.ResolvedLlmCredential?> ResolveAsync(Guid workspaceId, Guid? projectId, LlmProvider? preferredProvider = null, CancellationToken cancellationToken = default);
}

public interface ILlmCredentialStore
{
    Task<LlmCredential> SaveAsync(LlmCredential credential, CancellationToken cancellationToken = default);
    Task<LlmCredential?> GetByIdAsync(Guid credentialId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LlmCredential>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<WorkspaceLlmPolicy?> GetPolicyAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<WorkspaceLlmPolicy> SavePolicyAsync(WorkspaceLlmPolicy policy, CancellationToken cancellationToken = default);
}

/// <summary>Reversible encryption for tenant secrets. Distinct from <see cref="ISecretProvider"/>, which is read-only operator configuration.</summary>
public interface ISecretCipher
{
    (string CipherText, string KeyVersion) Encrypt(string plaintext);
    string Decrypt(string cipherText, string keyVersion);
}

/// <summary>Cheapest possible provider call that proves a credential works. Mocked in unit tests.</summary>
public interface ILlmCredentialProbe
{
    Task<LlmCredentialValidationResult> ProbeAsync(Guid credentialId, Llm.ResolvedLlmCredential credential, CancellationToken cancellationToken = default);
}

public sealed record RegisterLlmCredentialCommand(
    Guid WorkspaceId,
    Guid? ProjectId,
    string Name,
    LlmProvider Provider,
    LlmCredentialKind Kind,
    string Secret,
    string Model,
    string? Endpoint = null,
    IReadOnlyDictionary<string, string>? NonSecretSettings = null,
    bool IsDefault = false);

public interface ILlmCredentialService
{
    Task<LlmCredentialSummary> RegisterAsync(RegisterLlmCredentialCommand command, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<LlmCredentialSummary> RotateAsync(Guid workspaceId, Guid credentialId, string newSecret, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task RevokeAsync(Guid workspaceId, Guid credentialId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LlmCredentialSummary>> ListAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<LlmCredentialValidationResult> ValidateAsync(Guid workspaceId, Guid credentialId, Guid requestingUserId, CancellationToken cancellationToken = default);
    Task<WorkspaceLlmPolicy> GetPolicyAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default);
    /// <param name="allowedTools">Null leaves the tool allowlist unchanged; an empty list offers the model no tools.</param>
    Task<WorkspaceLlmPolicy> SetPolicyAsync(Guid workspaceId, bool allowPlatformFallback, Guid requestingUserId, IReadOnlyList<string>? allowedTools = null, CancellationToken cancellationToken = default);
}
