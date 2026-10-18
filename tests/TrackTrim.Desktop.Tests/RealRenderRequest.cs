using Serilog;

namespace TrackTrim.Desktop.Tests;

internal sealed record RealRenderRequest(string SourcePath, string ToolDirectory, string DataDirectory, ILogger Log);
