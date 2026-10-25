using System.Collections.Immutable;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;

namespace MovieSift.Application.Batches;

/// <summary>Validates the whole proposed destination set before any single-job execution.</summary>
internal static class BatchReviewValidator
{
    /// <summary>Checks approvals, source revisions, lexical aliases, collisions and each destination port.</summary>
    internal static async Task<BatchReviewResult> ValidateAsync(BatchValidationRequest request)
    {
        var pending = request.Snapshot.Items.Where(item => item.Included &&
            item.State is not (BatchItemState.Completed or BatchItemState.NoChange)).ToArray();

        if (pending.Length == 0)
        {
            return new(null, [], new(ErrorCode.InvalidSelection));
        }

        var sources = request.Snapshot.Items.Select(item => BatchSession.CanonicalPath(item.SourcePath))
            .ToHashSet(BatchSession.PathComparer);
        var destinations = pending.Where(item => item.ApprovedPlan is { IsNoChange: false })
            .GroupBy(item => BatchSession.CanonicalPath(item.ApprovedPlan!.DestinationPath), BatchSession.PathComparer)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(BatchSession.PathComparer);
        var failures = ImmutableArray.CreateBuilder<BatchItemFailure>();

        foreach (var item in pending)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var plan = item.ApprovedPlan;
            var missingApproval = item.State != BatchItemState.Ready || plan is null || item.Inspection is null;

            if (missingApproval)
            {
                failures.Add(new(item.Id, item.Failure ?? new(ErrorCode.StalePlan)));
                continue;
            }

            if (!plan!.IsNoChange && HasOutputConflict(plan.DestinationPath, sources, destinations))
            {
                failures.Add(new(item.Id, new(ErrorCode.InvalidDestination)));
                continue;
            }

            try
            {
                var revision = await request.SourceAccess.ReadRevisionAsync(new(item.SourcePath, request.CancellationToken));

                if (revision != plan.Inspection.Source)
                {
                    failures.Add(new(item.Id, new(ErrorCode.SourceChanged)));
                    continue;
                }

                if (plan.IsNoChange)
                {
                    continue;
                }

                await request.SourceAccess.ValidateDestinationAsync(new(revision, plan.DestinationPath, revision.Length,
                    request.CancellationToken, plan.OutputDirectoryRoot, plan.Inspection.Container));
            }
            catch (OperationFailureException exception)
            {
                failures.Add(new(item.Id, exception.Failure));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failures.Add(new(item.Id, new(ErrorCode.InvalidDestination, exception.Message)));
            }
        }

        if (failures.Count != 0)
        {
            return new(null, failures.ToImmutable());
        }

        var reviewed = pending.Select(item => new BatchReviewItem(item.Id, item.ApprovedPlan!)).ToImmutableArray();
        return new(new(Guid.NewGuid(), request.Snapshot.Revision, reviewed), []);
    }

    /// <summary>Compares only destinations that will actually create an output against every source and output.</summary>
    private static bool HasOutputConflict(string destination, HashSet<string> sources, HashSet<string> duplicates)
    {
        var canonical = BatchSession.CanonicalPath(destination);
        return sources.Contains(canonical) || duplicates.Contains(canonical);
    }
}

internal sealed record BatchValidationRequest(BatchSnapshot Snapshot, ISourceAccess SourceAccess, CancellationToken CancellationToken);
