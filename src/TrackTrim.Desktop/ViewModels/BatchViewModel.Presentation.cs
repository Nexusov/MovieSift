using TrackTrim.Application.Batches;

namespace TrackTrim.Desktop.ViewModels;

/// <summary>Owns contextual workspace presentation without adding another plan or job lifecycle.</summary>
public sealed partial class BatchViewModel
{
    private readonly Dictionary<Guid, BatchReviewRowViewModel> reviewRows = [];
    private bool isReviewOpen;
    private bool isDetailsOpen;
    private bool isRulesOpen;
    private string preparationNoticeKey = string.Empty;

    public bool HasItems => Items.Count != 0;
    public bool IsEmpty => !HasItems;
    public bool HasPendingInspections => Items.Any(item => item.Included && item.Snapshot.State == BatchItemState.PendingInspection);
    public bool CanPause => Snapshot.Phase == BatchPhase.Running && !Snapshot.PauseRequested;
    public bool CanCancel => IsBusy;
    public bool ShowNewQueue => HasItems;
    public bool IsEditingSurfaceVisible => !IsReviewOpen && !IsRulesOpen;
    public bool HasPreparationNotice => preparationNoticeKey.Length != 0;
    public string PreparationNoticeText => HasPreparationNotice ? Localization[preparationNoticeKey] : string.Empty;
    public int ReviewedCopyCount => Snapshot.Review?.Items.Count(item => !item.Plan.IsNoChange) ?? 0;
    public int ReviewedNoChangeCount => Snapshot.Review?.Items.Count(item => item.Plan.IsNoChange) ?? 0;
    public string ReviewSummaryText => Localization.Format(new("ReviewSummary",
        [FormatCount("ReviewChanged", ReviewedCopyCount), FormatCount("ReviewUnchanged", ReviewedNoChangeCount)]));
    public string RunActionText => ReviewedCopyCount == 0 ? Localization["RunNoChanges"] :
        Snapshot.Items.Any(item => item.Result?.PublishedPath is not null) ? Localization["RunContinue"] :
        FormatCount("RunCopies", ReviewedCopyCount);

    public bool IsReviewOpen
    {
        get => isReviewOpen;
        private set
        {
            SetProperty(ref isReviewOpen, value);
            OnPropertyChanged(nameof(IsEditingSurfaceVisible));
        }
    }

    public bool IsDetailsOpen
    {
        get => isDetailsOpen;
        set => SetProperty(ref isDetailsOpen, value);
    }

    public bool IsRulesOpen
    {
        get => isRulesOpen;
        set
        {
            SetProperty(ref isRulesOpen, value);
            OnPropertyChanged(nameof(IsEditingSurfaceVisible));
        }
    }

    /// <summary>Accepts dropped local paths through the same gated discovery and inspection operation as the picker.</summary>
    public Task AddDroppedFilesAsync(IReadOnlyList<string> paths)
    {
        if (!CanEdit || paths.Count == 0)
        {
            return Task.CompletedTask;
        }

        var capturedPaths = paths.ToArray();
        return ExecuteOwnedAsync(cancellationToken => DiscoverAsync(new(
            BatchInputKind.ExplicitFiles, capturedPaths, cancellationToken)));
    }

    /// <summary>Closes only the review surface; existing immutable review authority is not rewritten.</summary>
    private void CloseReview() => IsReviewOpen = false;

    /// <summary>Re-derives editable destinations through the existing item path policy.</summary>
    private void ApplyOutputFolder(string folder)
    {
        outputFolder = folder;
        preparationNoticeKey = string.Empty;

        foreach (var editor in Items)
        {
            editor.ApplyOutputFolder(folder);
        }
    }

    /// <summary>Reconciles per-file review rows without replacing stable editors or manufacturing plan authority.</summary>
    private void RefreshReviewRows()
    {
        var pending = Items.Where(item => item.Included &&
            item.Snapshot.State is not (BatchItemState.Completed or BatchItemState.NoChange)).ToArray();
        var liveIds = pending.Select(item => item.Id).ToHashSet();

        foreach (var removed in ReviewRows.Where(row => !liveIds.Contains(row.Item.Id)).ToArray())
        {
            ReviewRows.Remove(removed);
            reviewRows.Remove(removed.Item.Id);
        }

        foreach (var item in pending)
        {
            var validationFailure = reviewFailures.FirstOrDefault(issue => issue.ItemId == item.Id)?.Failure;
            var plan = Snapshot.Review?.Items.FirstOrDefault(reviewed => reviewed.ItemId == item.Id)?.Plan
                ?? item.ApprovedPlan;

            if (!reviewRows.TryGetValue(item.Id, out var row))
            {
                row = new(item, plan, validationFailure);
                reviewRows.Add(item.Id, row);
                ReviewRows.Add(row);
            }

            row.Update(plan, validationFailure);
        }
    }

    /// <summary>Uses explicit English/Russian cardinal forms for counts without changing media culture.</summary>
    private string FormatCount(string key, int count)
    {
        var suffix = count == 1 ? "One" : "Other";

        if (Localization.Language == "ru")
        {
            var finalDigit = count % 10;
            var finalTwoDigits = count % 100;
            var singular = finalDigit == 1 && finalTwoDigits != 11;
            var few = finalDigit is >= 2 and <= 4 && finalTwoDigits is not (>= 12 and <= 14);
            suffix = singular ? "One" : few ? "Few" : "Many";
        }

        return Localization.Format(new(key + suffix, [count]));
    }
}
