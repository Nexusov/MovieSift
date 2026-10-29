namespace MovieSift.Application.Inspection;

/// <summary>Describes actual inspection boundaries, never a predicted completion percentage.</summary>
public enum InspectionStage { CheckingSource, CheckingTools, ReadingContainer, CheckingStructure, ReadingMetadata, RecheckingSource }

/// <summary>Binds a stage notification to its independently inspected queue entry.</summary>
public sealed record BatchInspectionProgress(Guid ItemId, InspectionStage Stage);
