namespace PowerSync.Domain.Records
{
    /// <summary>
    /// Represents a PUT operation for a database record.
    /// </summary>
    public record PutOp
    {
        public string Op => "PUT";
        public required string Table { get; init; }
        public string? Id { get; init; }
        public required object Data { get; init; }
    }
}