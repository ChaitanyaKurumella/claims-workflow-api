namespace Claims.Api;
public static class DemoData
{
    public static void Seed(Store store)
    {
        if (store.List().Length > 0)
            return;
        var reviewer = new Actor("reviewer-01", "Reviewer");
        var supervisor = new Actor("supervisor-01", "Supervisor");
        var samples = new[]
        {
            ("CLM-2026-0841", 28450m, "Property", "Storm damage to commercial roof. Adjuster report received.", "Urgent"),
            ("CLM-2026-0842", 3840m, "Medical", "Outpatient procedure reimbursement. Invoice and coverage verified.", "Normal"),
            ("CLM-2026-0843", 12600m, "Liability", "Third-party premises incident. Legal review requested.", "Urgent"),
            ("CLM-2026-0844", 760m, "Travel", "Trip cancellation due to airline disruption.", "Normal"),
            ("CLM-2026-0845", 9200m, "Property", "Water damage assessment. Supporting photographs available.", "Normal"),
            ("CLM-2026-0846", 1840m, "Medical", "Duplicate billing review completed.", "Normal"),
            ("CLM-2026-0847", 42000m, "Liability", "Commercial equipment incident requiring supervisor sign-off.", "Urgent"),
            ("CLM-2026-0848", 2150m, "Travel", "Baggage loss after international connection.", "Normal")
        };
        for (int i = 0; i < samples.Length; i++)
        {
            var(reference, amount, category, description, priority) = samples[i];
            var c = store.Create(new(reference, amount, category, description, priority), reviewer, "seed-" + reference).Claim;
            if (i is 0 or 1 or 2 or 4 or 5 or 6)
                c = store.Apply(c.Id, "assign", new(c.Version), reviewer);
            if (i is 0 or 1 or 5 or 6)
                c = store.Apply(c.Id, i == 5 ? "reject" : "approve", new(c.Version, "Evidence and policy coverage reviewed."), reviewer);
            if (i == 6)
                store.Apply(c.Id, "approve", new(c.Version, "Independent supervisor assessment completed."), supervisor);
        }
    }
}
