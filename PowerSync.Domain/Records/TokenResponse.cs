namespace PowerSync.Domain.Records 
{
    public record TokenResponse
    {
        public string? Token { get; init; }
        public string? PowersyncUrl { get; init; }
    }
}