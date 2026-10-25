using Serilog;

namespace MovieSift.Desktop.Tests;

internal sealed record RealRenderRequest(string SourcePath, string ToolDirectory, string DataDirectory, ILogger Log);
