namespace Fabricate.Infrastructure.Configuration;

public sealed record SchemaProviderOptions
{
    public string Provider { get; set; } = "sqlite";
    public string ConnectionString { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = "fabricate";
}
