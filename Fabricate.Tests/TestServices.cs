using Fabricate.Application.Abstractions;
using Fabricate.Application.Governance;
using Fabricate.Application.Workspaces;
using Fabricate.Infrastructure.Repositories;

namespace Fabricate.Tests;

/// <summary>
/// Builds the platform services over in-memory repositories for unit tests. Since #65 these services take
/// repositories rather than holding their own state, so the wiring lives here instead of in every fixture.
/// </summary>
public sealed class TestServices
{
    public InMemoryAuditLogRepository AuditLogRepository { get; } = new();
    public InMemoryAccountRepository AccountRepository { get; } = new();
    public InMemoryWorkspaceRepository WorkspaceRepository { get; } = new();
    public InMemoryAccountGroupRepository AccountGroupRepository { get; } = new();
    public InMemoryInstructionVersionRepository InstructionRepository { get; } = new();
    public InMemoryConnectionRepository ConnectionRepository { get; } = new();
    public InMemoryAllowedDomainRepository AllowedDomainRepository { get; } = new();

    public IAuditLogService AuditLogService { get; }
    public WorkspaceService WorkspaceService { get; }
    public InstructionVersionService InstructionVersionService { get; }

    public TestServices()
    {
        AuditLogService = new AuditLogService(AuditLogRepository, AccountRepository);
        WorkspaceService = new WorkspaceService(WorkspaceRepository, AccountGroupRepository, AuditLogService);
        InstructionVersionService = new InstructionVersionService(InstructionRepository, WorkspaceService);
    }

    public AccountGroupService CreateAccountGroupService()
        => new(AccountGroupRepository, AccountRepository, AuditLogService);

    public AllowedDomainService CreateAllowedDomainService()
        => new(AllowedDomainRepository, AccountRepository, AuditLogService);

    public ConnectionCatalogService CreateConnectionCatalogService()
        => new(ConnectionRepository, WorkspaceService);
}
