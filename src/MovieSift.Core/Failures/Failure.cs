namespace MovieSift.Core.Failures;

public enum ErrorCode
{
    InvalidSource,
    UnsupportedContainer,
    UnsupportedFeature,
    ToolUnavailable,
    ToolVersionMismatch,
    InvalidToolOutput,
    ToolFailed,
    SourceChanged,
    InvalidSelection,
    ZeroAudioConfirmationRequired,
    InvalidDestination,
    DestinationExists,
    InsufficientSpace,
    PersistenceUnavailable,
    VerificationFailed,
    PublicationFailed,
    PublicationUncertain,
    OwnershipInvalid,
    ProcessTerminationFailed,
    Busy,
    StalePlan,
    Cancelled,
    IoFailure
}

public enum ErrorReason
{
    None,
    LinkedSegments,
    SegmentStructure,
    TrackType,
    HdrMetadata,
    ChapterReferences,
    OrderedChapters,
    TagTargets,
    DanglingTrackTags,
    RedirectedPath,
    NetworkPath,
    RegularFileRequired,
    BatchFileLimit,
    BatchEntryLimit,
    InvalidBatchInput,
    RecursiveOutputRequired,
    RecursiveRootConflict,
    BatchDepthLimit
}

public sealed record Failure(ErrorCode Code, string Detail = "", ErrorReason Reason = ErrorReason.None);
