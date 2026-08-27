namespace Logrr.Notify;

/// <summary>
/// Deletes a destination together with the rules that deliver to it (SPEC §10.1). A rule holds
/// a NOT NULL foreign key to its destination, so "delete the destination, keep the rules" is not
/// a state the schema can represent — the choice is cascade or refuse, and refusing would leave
/// an operator unable to remove a destination without hand-deleting every rule first.
/// </summary>
public sealed class DestinationDeleter(
    DestinationStore destinations,
    RuleStore rules,
    OccurrenceStore occurrences)
{
    /// <summary>What went with it, so the caller can say so.</summary>
    public sealed record Result(string DestinationId, string Name, int RulesDeleted);

    /// <summary>The rules that a delete would take with it — the confirmation the UI shows.</summary>
    public IReadOnlyList<Rule> RulesUsing(string destinationId) => rules.ListByDestination(destinationId);

    /// <summary>Delete a destination, or null when there is no such destination.</summary>
    public Result? Delete(string destinationId)
    {
        if (destinations.Get(destinationId) is not { } destination)
        {
            return null;
        }

        // Rules first, or the foreign key rejects the destination delete. Their occurrence
        // rollups are keyed by rule id and have no foreign key of their own, so nothing else
        // would ever clean them up.
        var affected = rules.ListByDestination(destinationId);
        foreach (var rule in affected)
        {
            occurrences.ResetForRule(rule.Id);
            rules.Delete(rule.Id);
        }

        destinations.Delete(destinationId);

        // Deliveries keep their destination id on purpose: the history is the audit trail, and
        // the dispatcher already dead-letters anything still queued for a destination that has
        // gone away.
        return new Result(destinationId, destination.Name, affected.Count);
    }
}
