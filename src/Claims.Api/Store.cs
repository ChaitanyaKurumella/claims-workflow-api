using System.Text.Json;
namespace Claims.Api;

public sealed record AuditEvent(DateTimeOffset At, string Action);
public sealed record StoredClaim(Claim Claim, AuditEvent[] History);

// One process owns this file. Commit the complete next state before publishing it to readers.
public sealed class Store
{
    private readonly object gate = new();
    private readonly string path;
    private Dictionary<Guid, StoredClaim> items;
    public Store(string path)
    {
        this.path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        items = File.Exists(this.path)
            ? JsonSerializer.Deserialize<Dictionary<Guid, StoredClaim>>(File.ReadAllText(this.path))
                ?? throw new InvalidDataException("Invalid store") : new();
    }
    public Claim[] List() { lock(gate) return items.Values.Select(x => x.Claim).OrderBy(x => x.Reference).ToArray(); }
    public StoredClaim? Get(Guid id) { lock(gate) return items.GetValueOrDefault(id); }
    public Claim? Create(CreateClaim input)
    {
        lock(gate)
        {
            var reference = input.Reference!.Trim();
            if(items.Values.Any(x => string.Equals(x.Claim.Reference, reference, StringComparison.OrdinalIgnoreCase))) return null;
            var claim = new Claim(Guid.NewGuid(), reference, input.Amount, ClaimStatus.Submitted);
            Commit(new(items) { [claim.Id] = new(claim, [new(DateTimeOffset.UtcNow, "Submitted")]) });
            return claim;
        }
    }
    public (int Status, Claim? Claim) Decide(Guid id, string outcome)
    {
        lock(gate)
        {
            if(!items.TryGetValue(id, out var stored)) return (404, null);
            if(!ClaimRules.CanDecide(stored.Claim)) return (409, null);
            var claim = stored.Claim with { Status = outcome == "approve" ? ClaimStatus.Approved : ClaimStatus.Rejected };
            Commit(new(items) { [id] = new(claim, [..stored.History, new(DateTimeOffset.UtcNow, claim.Status.ToString())]) });
            return (200, claim);
        }
    }
    private void Commit(Dictionary<Guid, StoredClaim> next)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(next));
        File.Move(temp, path, true);
        items = next;
    }
}
