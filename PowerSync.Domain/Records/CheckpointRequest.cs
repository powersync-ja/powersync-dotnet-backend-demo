namespace PowerSync.Domain.Records
{
    public record CheckpointRequest
    {
        public string? UserId { get; set; }
        public string? ClientId { get; set; }
    }
}