namespace Claims.Api;

public enum ClaimStatus { Submitted, InReview, PendingSupervisor, Approved, Rejected }
public sealed record Claim(Guid Id, string Reference, decimal Amount, ClaimStatus Status,
    string Category, string Description, string Priority, string? Assignee, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset DueAt, bool Escalated);
public sealed record CreateClaim(string? Reference, decimal Amount, string Category = "Medical", string Description = "", string Priority = "Normal");
public sealed record Command(int Version, string? Note = null);
public sealed record Actor(string Id, string Role);
public sealed record AuditEvent(long Sequence, DateTimeOffset At, string Action, string Actor, string Note, int Version);
public sealed record StoredClaim(Claim Claim, AuditEvent[] History);
public sealed record OutboxEvent(long Id, Guid ClaimId, string Type, string Payload, int Attempts, string Status, DateTimeOffset NextAttempt, string? LastError);
public sealed class RuleException(int status, string message) : Exception(message) { public int Status { get; } = status; }
public static class ClaimRules
{
    public static string? Validate(CreateClaim input) =>
        string.IsNullOrWhiteSpace(input.Reference) || input.Reference.Trim().Length > 64 ? "Reference must contain 1–64 characters."
        : input.Amount <= 0 || input.Amount > 1_000_000 || decimal.Round(input.Amount, 2) != input.Amount ? "Amount must be 0.01–1,000,000 with at most two decimal places."
        : input.Category is not ("Medical" or "Property" or "Travel" or "Liability") ? "Select a supported category."
        : input.Priority is not ("Normal" or "Urgent") ? "Priority must be Normal or Urgent."
        : input.Description is null || input.Description.Length > 2000 ? "Description must be at most 2000 characters." : null;
}
