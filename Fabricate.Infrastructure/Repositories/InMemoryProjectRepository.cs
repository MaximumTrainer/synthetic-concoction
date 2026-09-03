using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

public sealed class InMemoryProjectRepository : IProjectRepository
{
    private readonly List<Project> _projects = [];

    public Task<Project> SaveAsync(Project project, CancellationToken cancellationToken = default)
    {
        var index = _projects.FindIndex(p => p.Id == project.Id);
        if (index >= 0)
            _projects[index] = project;
        else
            _projects.Add(project);

        return Task.FromResult(project);
    }

    public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_projects.Find(p => p.Id == id));

    public Task<IReadOnlyList<Project>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Project>>(_projects.Where(p => p.WorkspaceId == workspaceId).ToArray());
}
