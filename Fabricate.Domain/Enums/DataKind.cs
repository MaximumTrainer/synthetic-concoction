namespace Fabricate.Domain.Enums;

public enum DataKind
{
    Unknown = 0,
    Boolean,
    Integer,
    Long,
    Decimal,
    Double,
    String,
    Guid,
    Date,
    DateTime,
    Json,
    Binary,

    // Semantic kinds added in issue #14
    Email,
    Phone,
    Name,
    FirstName,
    LastName,
    Address,
    PostalCode,
    CountryCode,
    Url,
    IpAddress,
    Currency,
    CompanyName,
    Text,
    Uuid,
    TimestampTz
}
