namespace Claims.Api;
// Pure policy: no database, HTTP, or clock dependencies.
public static class WorkflowPolicy
{
    public static Claim Decide(Claim claim, string action, Command command, Actor actor)
    {
        if (command.Note?.Length > 1000)
            throw new RuleException(400, "Note must be at most 1000 characters.");
        bool supervisor = actor.Role is "Supervisor" or "Administrator";
        if (claim.Status is ClaimStatus.Approved or ClaimStatus.Rejected)
            throw new RuleException(409, "Final decisions cannot be changed.");
        var next = claim;
        switch (action)
        {
            case "assign":
                if (claim.Status != ClaimStatus.Submitted)
                    throw new RuleException(409, "Only unassigned intake claims can be claimed.");
                next = claim with
                {
                    Status = ClaimStatus.InReview,
                    Assignee = actor.Id
                };
                break;
            case "release":
                if (claim.Status != ClaimStatus.InReview)
                    throw new RuleException(409, "Claim is not in review.");
                if (claim.Assignee != actor.Id && !supervisor)
                    throw new RuleException(403, "Only the assignee or a supervisor can release this claim.");
                next = claim with
                {
                    Status = ClaimStatus.Submitted,
                    Assignee = null
                };
                break;
            case "approve":
            case "reject":
                if (string.IsNullOrWhiteSpace(command.Note))
                    throw new RuleException(400, "A decision rationale is required.");
                if (claim.Status == ClaimStatus.PendingSupervisor)
                {
                    if (!supervisor)
                        throw new RuleException(403, "Supervisor authorization is required.");
                    if (claim.Assignee == actor.Id)
                        throw new RuleException(403, "A different person must perform the second approval.");
                    next = claim with
                    {
                        Status = action == "approve" ? ClaimStatus.Approved : ClaimStatus.Rejected
                    };
                }
                else
                {
                    if (claim.Status != ClaimStatus.InReview)
                        throw new RuleException(409, "Assign the claim before deciding.");
                    if (claim.Assignee != actor.Id)
                        throw new RuleException(403, "Only the assigned reviewer can decide this claim.");
                    next = claim with
                    {
                        Status = action == "reject" ? ClaimStatus.Rejected : claim.Amount >= 10000 ? ClaimStatus.PendingSupervisor : ClaimStatus.Approved
                    };
                }

                break;
            default:
                throw new RuleException(404, "Unknown workflow action.");
        }

        return next with
        {
            Version = claim.Version + 1
        };
    }
}
