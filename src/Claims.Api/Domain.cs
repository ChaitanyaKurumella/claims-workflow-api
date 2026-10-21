namespace Claims.Api;

public enum ClaimStatus { Submitted, Approved, Rejected }
public sealed record Claim(Guid Id, string Reference, decimal Amount, ClaimStatus Status);
public sealed record CreateClaim(string? Reference, decimal Amount);
public sealed record Decision(string? Outcome);

public static class ClaimRules
{
    public static string? Validate(CreateClaim input) =>
        string.IsNullOrWhiteSpace(input.Reference) || input.Reference.Length > 64
            ? "Reference must contain 1–64 characters."
            : input.Amount <= 0 || input.Amount > 1_000_000
                ? "Amount must be greater than zero and at most 1,000,000."
                : decimal.Round(input.Amount, 2) != input.Amount
                    ? "Amount must have at most two decimal places." : null;

    public static bool CanDecide(Claim claim) => claim.Status == ClaimStatus.Submitted;
}
